//! Затвор приёма соединений рабочего процесса и его завершение (.NET `AcceptGate.cs`: `AcceptGate`, `GatedListener`, `DrainState`).
//!
//! Слушающий сокет общий (его открыл супервизор, он никогда не закрывается), а *принимает* из него тот процесс, у которого затвор
//! открыт: новый открывает его, когда готов, старый закрывает и заканчивает начатое. Соединения, которых никто пока не принял, ждут в
//! очереди ядра — отказов при замене нет. `accept` у tokio отменяем без потери соединения: снятый приём оставляет его в очереди.
//!
//! [`DrainState`] считает вызовы в работе (до конца отправки тела ответа — у MCP это поток SSE), при завершении закрывает соединение
//! после ответа (`Connection: close`: клиент придёт к новому процессу), а в последней фазе отвечает 503 с `Retry-After: 1`.
use axum::body::Body;
use axum::extract::{Request, State};
use axum::http::{HeaderValue, StatusCode, header};
use axum::middleware::Next;
use axum::response::{IntoResponse, Response};
use guarded_body::Guarded;
use std::io;
use std::net::SocketAddr;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicI64, AtomicUsize, Ordering};
use std::time::{Duration, Instant};
use tokio::net::{TcpListener, TcpStream};
use tokio::sync::watch;
use tracing::{error, warn};

/// Затвор: открыт — процесс принимает соединения из общего сокета.
pub struct AcceptGate {
    open: watch::Sender<bool>,
}

impl AcceptGate {
    pub fn new() -> AcceptGate {
        AcceptGate {
            open: watch::channel(false).0,
        }
    }

    pub fn is_open(&self) -> bool {
        *self.open.borrow()
    }

    pub fn open(&self) {
        self.open.send_replace(true);
    }

    /// Перестаёт принимать новые соединения; принятые продолжают работать.
    pub fn close(&self) {
        self.open.send_replace(false);
    }

    fn subscribe(&self) -> watch::Receiver<bool> {
        self.open.subscribe()
    }
}

impl Default for AcceptGate {
    fn default() -> Self {
        Self::new()
    }
}

/// Слушатель axum с затвором: пока затвор закрыт, соединения не принимаются (`accept` ждёт). Закрыть сокет здесь нельзя — он
/// общий с супервизором и новым процессом: процесс выходит, и закрывается только его дескриптор.
pub struct GatedListener {
    inner: TcpListener,
    gate: watch::Receiver<bool>,
}

impl GatedListener {
    pub fn new(inner: TcpListener, gate: &AcceptGate) -> GatedListener {
        GatedListener {
            inner,
            gate: gate.subscribe(),
        }
    }
}

impl axum::serve::Listener for GatedListener {
    type Io = TcpStream;
    type Addr = SocketAddr;

    async fn accept(&mut self) -> (TcpStream, SocketAddr) {
        loop {
            if self.gate.wait_for(|open| *open).await.is_err() {
                // Затвора больше нет: процесс закрывается, принимать нечего.
                std::future::pending::<()>().await;
            }
            // Затвор закроют — незавершённый приём снимается (приём, который уже получил соединение, его отдаёт: оно обслужится).
            let accepted = tokio::select! {
                biased;
                accepted = self.inner.accept() => Some(accepted),
                _ = self.gate.wait_for(|open| !*open) => None,
            };
            match accepted {
                Some(Ok((stream, address))) => {
                    let _ = stream.set_nodelay(true);
                    return (stream, address);
                }
                Some(Err(e)) => accept_error(e).await,
                None => {}
            }
        }
    }

    fn local_addr(&self) -> io::Result<SocketAddr> {
        self.inner.local_addr()
    }
}

/// Как `axum::serve`: ошибки соединения (клиент ушёл до приёма) пропускаются, нехватка ресурсов — пауза 1 с.
async fn accept_error(e: io::Error) {
    if matches!(
        e.kind(),
        io::ErrorKind::ConnectionRefused | io::ErrorKind::ConnectionAborted | io::ErrorKind::ConnectionReset
    ) {
        return;
    }
    error!("Accept failed: {e}");
    tokio::time::sleep(Duration::from_secs(1)).await;
}

/// Состояние завершения старого процесса: считает вызовы в работе, а на закрытии отвечает повторяемой ошибкой. Вызовы, пришедшие
/// по уже открытым соединениям, обслуживаются как обычно — но ответ закрывает соединение.
pub struct DrainState {
    active: AtomicUsize,
    /// Миллисекунды от `origin` до последнего конца вызова.
    last_activity: AtomicI64,
    origin: Instant,
    draining: AtomicBool,
    closing: AtomicBool,
}

impl DrainState {
    pub fn new() -> DrainState {
        DrainState {
            active: AtomicUsize::new(0),
            last_activity: AtomicI64::new(0),
            origin: Instant::now(),
            draining: AtomicBool::new(false),
            closing: AtomicBool::new(false),
        }
    }

    pub fn draining(&self) -> bool {
        self.draining.load(Ordering::SeqCst)
    }

    /// Последняя фаза: процесс закрывается, новые вызовы получают 503 с `Retry-After`.
    pub fn closing(&self) -> bool {
        self.closing.load(Ordering::SeqCst)
    }

    pub fn active(&self) -> usize {
        self.active.load(Ordering::SeqCst)
    }

    fn now(&self) -> i64 {
        i64::try_from(self.origin.elapsed().as_millis()).unwrap_or(i64::MAX)
    }

    fn touch(&self) {
        self.last_activity.store(self.now(), Ordering::SeqCst);
    }

    pub fn idle(&self) -> Duration {
        Duration::from_millis(u64::try_from(self.now() - self.last_activity.load(Ordering::SeqCst)).unwrap_or(0))
    }

    pub fn begin_drain(&self) {
        self.draining.store(true, Ordering::SeqCst);
        // Отсчёт тишины — с этого момента: соединение, принятое за миг до закрытия затвора, ещё пришлёт свой вызов.
        self.touch();
    }

    pub fn begin_closing(&self) {
        self.closing.store(true, Ordering::SeqCst);
    }

    fn enter(self: &Arc<Self>) -> CallGuard {
        self.active.fetch_add(1, Ordering::SeqCst);
        CallGuard { drain: self.clone() }
    }

    /// Ждёт, пока вызовов в работе не будет `quiet` подряд, но не дольше `timeout`. Возвращает, сколько вызовов осталось (0 — всё).
    pub async fn wait_quiet(&self, quiet: Duration, timeout: Duration) -> usize {
        let deadline = Instant::now() + timeout;
        loop {
            if self.active() == 0 && self.idle() >= quiet {
                return 0;
            }
            if Instant::now() >= deadline {
                warn!(
                    "{} call(s) are still running after {} s: they are aborted",
                    self.active(),
                    seconds(timeout)
                );
                return self.active();
            }
            tokio::time::sleep(Duration::from_millis(50)).await;
        }
    }
}

impl Default for DrainState {
    fn default() -> Self {
        Self::new()
    }
}

/// `{Seconds:0}` .NET: секунды, округлённые до целого.
pub fn seconds(duration: Duration) -> u64 {
    duration.as_secs_f64().round() as u64
}

/// Вызов в работе: живёт до конца отправки тела ответа.
pub struct CallGuard {
    drain: Arc<DrainState>,
}

impl Drop for CallGuard {
    fn drop(&mut self) {
        self.drain.touch();
        self.drain.active.fetch_sub(1, Ordering::SeqCst);
    }
}

/// Промежуточный слой рабочего процесса (`DrainState.Invoke`): самый внешний, раньше фильтра «только с этой машины».
pub async fn layer(State(drain): State<Arc<DrainState>>, request: Request, next: Next) -> Response {
    if drain.closing() {
        let mut response = (
            StatusCode::SERVICE_UNAVAILABLE,
            [(header::CONTENT_TYPE, HeaderValue::from_static("application/json; charset=utf-8"))],
            "{\"error\":\"[unavailable] The MCP server process is being replaced: repeat the request\"}",
        )
            .into_response();
        let headers = response.headers_mut();
        headers.insert(header::RETRY_AFTER, HeaderValue::from_static("1"));
        headers.insert(header::CONNECTION, HeaderValue::from_static("close"));
        return response;
    }

    let guard = drain.enter();
    let mut response = next.run(request).await;
    if drain.draining() {
        response.headers_mut().insert(header::CONNECTION, HeaderValue::from_static("close"));
    }
    let (parts, body) = response.into_parts();
    Response::from_parts(parts, Body::new(Guarded::new(body, guard)))
}

/// Тело ответа, которое держит [`CallGuard`] до конца отправки (или обрыва).
mod guarded_body {
    use super::CallGuard;
    use axum::body::{Body, Bytes};
    use http_body::{Frame, SizeHint};
    use std::pin::Pin;
    use std::task::{Context, Poll};

    pub struct Guarded {
        inner: Body,
        guard: Option<CallGuard>,
    }

    impl Guarded {
        pub fn new(inner: Body, guard: CallGuard) -> Guarded {
            Guarded { inner, guard: Some(guard) }
        }
    }

    impl http_body::Body for Guarded {
        type Data = Bytes;
        type Error = axum::Error;

        fn poll_frame(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<Option<Result<Frame<Bytes>, axum::Error>>> {
            let polled = Pin::new(&mut self.inner).poll_frame(cx);
            if let Poll::Ready(None) = polled {
                self.guard = None;
            }
            polled
        }

        fn is_end_stream(&self) -> bool {
            self.inner.is_end_stream()
        }

        fn size_hint(&self) -> SizeHint {
            self.inner.size_hint()
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn quiet_is_counted_from_the_start_of_draining_and_calls_hold_it() {
        let drain = Arc::new(DrainState::new());
        let call = drain.enter();
        drain.begin_drain();
        assert!(drain.draining());
        assert_eq!(drain.active(), 1);
        let waiting = {
            let drain = drain.clone();
            tokio::spawn(async move { drain.wait_quiet(Duration::from_millis(100), Duration::from_secs(5)).await })
        };
        tokio::time::sleep(Duration::from_millis(300)).await;
        assert!(!waiting.is_finished());
        drop(call);
        assert_eq!(waiting.await.unwrap(), 0);
        assert!(drain.idle() >= Duration::from_millis(100));

        let _stuck = drain.enter();
        assert_eq!(drain.wait_quiet(Duration::ZERO, Duration::from_millis(100)).await, 1);
    }

    #[tokio::test]
    async fn the_gate_holds_connections_in_the_queue_until_it_opens() {
        use axum::serve::Listener as _;
        let gate = AcceptGate::new();
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = listener.local_addr().unwrap();
        let mut gated = GatedListener::new(listener, &gate);
        let _client = TcpStream::connect(address).await.unwrap();
        assert!(
            tokio::time::timeout(Duration::from_millis(200), gated.accept()).await.is_err(),
            "accepted while closed"
        );
        gate.open();
        assert!(gate.is_open());
        tokio::time::timeout(Duration::from_secs(5), gated.accept()).await.unwrap();
        gate.close();
        let _second = TcpStream::connect(address).await.unwrap();
        assert!(tokio::time::timeout(Duration::from_millis(200), gated.accept()).await.is_err());
        gate.open();
        tokio::time::timeout(Duration::from_secs(5), gated.accept()).await.unwrap();
    }
}
