//! Как слушающий сокет супервизора попадает в рабочий процесс (.NET `ListenerHandoff.cs`, `UnixFd` в `WorkerProtocol.cs`).
//!
//! На Unix — наследованием дескриптора: супервизор снимает с сокета `FD_CLOEXEC`, рабочий процесс получает его номер в
//! `--listen-fd N` и сразу ставит `FD_CLOEXEC` на свою копию (процессы, которые запустит сам сервер, сокет не получают). На Windows
//! сокет *дублируют в процесс-приёмник* (`WSADuplicateSocketW`): супервизор запускает рабочий процесс, делает описание сокета для его
//! номера и передаёт его в `hello` (`listenSocket: "wsa:<base64 WSAPROTOCOL_INFOW>"`), рабочий собирает из него свой дескриптор
//! (`WSASocketW`). Очередь соединений у всех копий общая. `TASKER_MCP_HANDOFF=message` заставляет супервизор Unix передавать номер
//! дескриптора сообщением (`fd:N`), как на Windows, — для проверки этого пути на macOS и Linux.
//!
//! Windows-ветка написана по C# и не проверена.
use std::io;
use std::net::TcpListener;

pub const FD_PREFIX: &str = "fd:";
pub const WSA_PREFIX: &str = "wsa:";
/// Размер `WSAPROTOCOL_INFOW` в байтах: одинаков на x86, x64 и arm64.
pub const PROTOCOL_INFO_SIZE: usize = 628;
pub const MODE_VARIABLE: &str = "TASKER_MCP_HANDOFF";
pub const HANDOFF_ARGUMENT: &str = "--listen-handoff";
pub const FD_ARGUMENT: &str = "--listen-fd";

/// Описание сокета из `hello`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Described {
    Fd(i32),
    ProtocolInfo(Vec<u8>),
}

/// Разбирает поле `listenSocket`; None — оно пусто или неизвестного вида (сокет рабочий процесс получил иначе).
pub fn parse(description: Option<&str>) -> Option<Described> {
    let description = description.filter(|d| !d.is_empty())?;
    if let Some(number) = description.strip_prefix(FD_PREFIX) {
        return match number.parse::<i32>() {
            Ok(fd) if fd >= 0 => Some(Described::Fd(fd)),
            _ => None,
        };
    }
    let encoded = description.strip_prefix(WSA_PREFIX)?;
    let bytes = base64_decode(encoded)?;
    (bytes.len() == PROTOCOL_INFO_SIZE).then_some(Described::ProtocolInfo(bytes))
}

pub fn format_fd(fd: i32) -> String {
    format!("{FD_PREFIX}{fd}")
}

pub fn format_wsa(protocol_info: &[u8]) -> io::Result<String> {
    if protocol_info.len() != PROTOCOL_INFO_SIZE {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            format!("WSAPROTOCOL_INFOW must be {PROTOCOL_INFO_SIZE} bytes, got {}", protocol_info.len()),
        ));
    }
    Ok(format!("{WSA_PREFIX}{}", base64_encode(protocol_info)))
}

/// Способ передачи на этой системе (`ListenerHandoff.ForThisSystem`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Handoff {
    /// Unix: наследуемый дескриптор, номер — в `--listen-fd`.
    UnixInherit,
    /// Unix: наследуемый дескриптор, номер — в `hello` (`fd:N`), аргумент `--listen-handoff`.
    UnixMessage,
    /// Windows: `WSADuplicateSocketW` в процесс-приёмник, описание — в `hello`.
    Windows,
}

impl Handoff {
    pub fn for_this_system() -> Handoff {
        if cfg!(windows) {
            Handoff::Windows
        } else if std::env::var(MODE_VARIABLE).is_ok_and(|v| v.eq_ignore_ascii_case("message")) {
            Handoff::UnixMessage
        } else {
            Handoff::UnixInherit
        }
    }

    /// Что дописать рабочему процессу в командную строку.
    pub fn worker_arguments(self, listener: &TcpListener) -> Vec<String> {
        match self {
            Handoff::UnixInherit => vec![FD_ARGUMENT.to_string(), raw(listener).to_string()],
            Handoff::UnixMessage | Handoff::Windows => vec![HANDOFF_ARGUMENT.to_string()],
        }
    }

    /// Подготовка сокета после открытия (на Unix — снять close-on-exec).
    pub fn prepare(self, listener: &TcpListener) -> io::Result<()> {
        match self {
            Handoff::UnixInherit | Handoff::UnixMessage => set_inheritable(raw(listener), true),
            Handoff::Windows => Ok(()),
        }
    }

    /// Описание сокета для рабочего процесса `worker_pid` (поле `hello`) или None, если оно не нужно.
    pub fn export(self, listener: &TcpListener, worker_pid: u32) -> io::Result<Option<String>> {
        match self {
            Handoff::UnixInherit => Ok(None),
            Handoff::UnixMessage => Ok(Some(format_fd(raw(listener)))),
            Handoff::Windows => {
                let _ = worker_pid;
                #[cfg(windows)]
                {
                    format_wsa(&windows::export(listener, worker_pid)?).map(Some)
                }
                #[cfg(not(windows))]
                {
                    Err(io::Error::other("A WinSock socket description cannot be made outside Windows"))
                }
            }
        }
    }
}

/// Номер дескриптора (Unix) или значение SOCKET (Windows), которым рабочий процесс откроет слушатель.
#[cfg(unix)]
fn raw(listener: &TcpListener) -> i32 {
    use std::os::fd::AsRawFd as _;
    listener.as_raw_fd()
}

#[cfg(windows)]
fn raw(listener: &TcpListener) -> i32 {
    use std::os::windows::io::AsRawSocket as _;
    listener.as_raw_socket() as i32
}

/// Слушающий сокет рабочего процесса: из `hello` (`listenSocket`) или из `--listen-fd`.
pub fn import(description: Option<&str>, listen_fd: Option<i32>) -> io::Result<TcpListener> {
    match parse(description) {
        Some(Described::Fd(fd)) => return from_fd(fd),
        Some(Described::ProtocolInfo(info)) => {
            #[cfg(windows)]
            {
                return windows::import(&info);
            }
            #[cfg(not(windows))]
            {
                let _ = info;
                return Err(io::Error::other("A WinSock socket description cannot be used outside Windows"));
            }
        }
        None => {}
    }
    match listen_fd {
        Some(fd) if fd >= 0 => from_fd(fd),
        _ => Err(io::Error::other("The supervisor did not give the worker a listening socket")),
    }
}

#[cfg(unix)]
fn from_fd(fd: i32) -> io::Result<TcpListener> {
    use std::os::fd::FromRawFd as _;
    // SAFETY: дескриптор унаследован от супервизора именно для этого процесса и больше нигде в нём не используется.
    Ok(unsafe { TcpListener::from_raw_fd(fd) })
}

#[cfg(windows)]
fn from_fd(fd: i32) -> io::Result<TcpListener> {
    use std::os::windows::io::FromRawSocket as _;
    // SAFETY: значение SOCKET передано супервизором для этого процесса.
    Ok(unsafe { TcpListener::from_raw_socket(fd as u64) })
}

/// Дескриптор остаётся открытым в дочерних процессах (`inheritable`) или закрывается при запуске программы (`FD_CLOEXEC`).
#[cfg(unix)]
pub fn set_inheritable(fd: i32, inheritable: bool) -> io::Result<()> {
    use rustix::io::{FdFlags, fcntl_getfd, fcntl_setfd};
    // SAFETY: дескриптор открыт на всё время вызова (его держит вызывающий).
    let borrowed = unsafe { std::os::fd::BorrowedFd::borrow_raw(fd) };
    let flags = fcntl_getfd(borrowed)
        .map_err(|e| io::Error::other(format!("fcntl(F_GETFD) failed for descriptor {fd}: errno {}", e.raw_os_error())))?;
    let flags = if inheritable {
        flags - FdFlags::CLOEXEC
    } else {
        flags | FdFlags::CLOEXEC
    };
    fcntl_setfd(borrowed, flags)
        .map_err(|e| io::Error::other(format!("fcntl(F_SETFD) failed for descriptor {fd}: errno {}", e.raw_os_error())))
}

#[cfg(not(unix))]
pub fn set_inheritable(_fd: i32, _inheritable: bool) -> io::Result<()> {
    Ok(())
}

#[cfg(unix)]
pub fn is_inheritable(fd: i32) -> bool {
    // SAFETY: только чтение флагов дескриптора.
    let borrowed = unsafe { std::os::fd::BorrowedFd::borrow_raw(fd) };
    rustix::io::fcntl_getfd(borrowed).is_ok_and(|flags| !flags.contains(rustix::io::FdFlags::CLOEXEC))
}

/// Слушающий сокет супервизора на `127.0.0.1:port` с очередью 512. На Unix — `SO_REUSEADDR` (порт в `TIME_WAIT` не мешает), на
/// Windows `SO_REUSEADDR` разрешил бы второму сокету занять тот же порт — там порт берётся исключительно (`SO_EXCLUSIVEADDRUSE`).
#[cfg(unix)]
pub fn bind(port: u16) -> io::Result<TcpListener> {
    use rustix::net::{AddressFamily, SocketType, bind as bind_socket, listen, socket, sockopt};
    let fd = socket(AddressFamily::INET, SocketType::STREAM, None)?;
    // Наследуемым сокет делает только `Handoff::prepare`: до того — как любой другой дескриптор (macOS не знает SOCK_CLOEXEC).
    set_inheritable(std::os::fd::AsRawFd::as_raw_fd(&fd), false)?;
    sockopt::set_socket_reuseaddr(&fd, true)?;
    bind_socket(&fd, &std::net::SocketAddrV4::new(std::net::Ipv4Addr::LOCALHOST, port))?;
    listen(&fd, 512)?;
    Ok(TcpListener::from(fd))
}

#[cfg(windows)]
pub fn bind(port: u16) -> io::Result<TcpListener> {
    windows::bind(port)
}

/// Base64 со стандартным алфавитом и `=` (`Convert.ToBase64String`).
pub fn base64_encode(bytes: &[u8]) -> String {
    const ALPHABET: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::with_capacity(bytes.len().div_ceil(3) * 4);
    for chunk in bytes.chunks(3) {
        let mut buffer = [0u8; 3];
        buffer[..chunk.len()].copy_from_slice(chunk);
        let n = (u32::from(buffer[0]) << 16) | (u32::from(buffer[1]) << 8) | u32::from(buffer[2]);
        for i in 0..4 {
            if i <= chunk.len() {
                out.push(ALPHABET[((n >> (18 - 6 * i)) & 0x3F) as usize] as char);
            } else {
                out.push('=');
            }
        }
    }
    out
}

/// `Convert.FromBase64String`: None — не base64.
pub fn base64_decode(text: &str) -> Option<Vec<u8>> {
    let clean: Vec<u8> = text.bytes().filter(|b| !b.is_ascii_whitespace()).collect();
    if clean.len() % 4 != 0 {
        return None;
    }
    let value = |c: u8| -> Option<u32> {
        Some(match c {
            b'A'..=b'Z' => u32::from(c - b'A'),
            b'a'..=b'z' => u32::from(c - b'a') + 26,
            b'0'..=b'9' => u32::from(c - b'0') + 52,
            b'+' => 62,
            b'/' => 63,
            _ => return None,
        })
    };
    let mut out = Vec::with_capacity(clean.len() / 4 * 3);
    for (index, chunk) in clean.chunks(4).enumerate() {
        let last = index == clean.len() / 4 - 1;
        let padding = chunk.iter().rev().take_while(|&&c| c == b'=').count();
        if padding > 2 || (padding > 0 && !last) {
            return None;
        }
        let mut n = 0u32;
        for &c in &chunk[..4 - padding] {
            n = (n << 6) | value(c)?;
        }
        n <<= 6 * padding as u32;
        let bytes = [(n >> 16) as u8, (n >> 8) as u8, n as u8];
        out.extend_from_slice(&bytes[..3 - padding]);
    }
    Some(out)
}

/// Вызовы WinSock: дублирование слушающего сокета в другой процесс и исключительный порт. Не проверено.
#[cfg(windows)]
mod windows {
    use super::PROTOCOL_INFO_SIZE;
    use std::io;
    use std::net::TcpListener;
    use std::os::windows::io::{AsRawSocket as _, FromRawSocket as _};
    use windows_sys::Win32::Networking::WinSock::{
        AF_INET, FROM_PROTOCOL_INFO, IN_ADDR, IN_ADDR_0, INVALID_SOCKET, IPPROTO_TCP, SO_EXCLUSIVEADDRUSE, SOCK_STREAM, SOCKADDR,
        SOCKADDR_IN, SOCKET, SOL_SOCKET, WSA_FLAG_NO_HANDLE_INHERIT, WSA_FLAG_OVERLAPPED, WSADuplicateSocketW, WSAGetLastError,
        WSAPROTOCOL_INFOW, WSASocketW, bind as ws_bind, closesocket, listen as ws_listen, setsockopt,
    };

    /// WinSock инициализирует std при первом сокете: без него `WSASocketW` вернёт `WSANOTINITIALISED`.
    fn init() {
        let _ = std::net::UdpSocket::bind("127.0.0.1:0");
    }

    fn last_error(what: &str) -> io::Error {
        // SAFETY: без аргументов.
        let code = unsafe { WSAGetLastError() };
        io::Error::other(format!("{what} failed: {}", io::Error::from_raw_os_error(code)))
    }

    /// Описание сокета для процесса `process_id`; исходный сокет остаётся открытым.
    pub fn export(listener: &TcpListener, process_id: u32) -> io::Result<Vec<u8>> {
        assert_eq!(std::mem::size_of::<WSAPROTOCOL_INFOW>(), PROTOCOL_INFO_SIZE);
        // SAFETY: структура — простые данные, её заполняет WSADuplicateSocketW.
        let mut info: WSAPROTOCOL_INFOW = unsafe { std::mem::zeroed() };
        // SAFETY: сокет открыт, указатель на структуру верный.
        if unsafe { WSADuplicateSocketW(listener.as_raw_socket() as SOCKET, process_id, &mut info) } != 0 {
            return Err(last_error(&format!("WSADuplicateSocket for process {process_id}")));
        }
        // SAFETY: структура фиксированного размера без указателей наружу.
        let bytes = unsafe { std::slice::from_raw_parts((&info as *const WSAPROTOCOL_INFOW).cast::<u8>(), PROTOCOL_INFO_SIZE) };
        Ok(bytes.to_vec())
    }

    /// Собирает дескриптор того же сокета в этом процессе.
    pub fn import(protocol_info: &[u8]) -> io::Result<TcpListener> {
        init();
        // SAFETY: размер проверен при разборе; структура — простые данные.
        let mut info: WSAPROTOCOL_INFOW = unsafe { std::mem::zeroed() };
        unsafe {
            std::ptr::copy_nonoverlapping(
                protocol_info.as_ptr(),
                (&mut info as *mut WSAPROTOCOL_INFOW).cast::<u8>(),
                PROTOCOL_INFO_SIZE,
            );
        }
        // SAFETY: описание сокета получено от WSADuplicateSocketW супервизора для этого процесса.
        let socket = unsafe {
            WSASocketW(
                FROM_PROTOCOL_INFO,
                FROM_PROTOCOL_INFO,
                FROM_PROTOCOL_INFO,
                &info,
                0,
                WSA_FLAG_OVERLAPPED | WSA_FLAG_NO_HANDLE_INHERIT,
            )
        };
        if socket == INVALID_SOCKET {
            return Err(last_error("WSASocket from the protocol information of the supervisor"));
        }
        // SAFETY: сокет только что создан и принадлежит этому процессу.
        Ok(unsafe { TcpListener::from_raw_socket(socket as u64) })
    }

    pub fn bind(port: u16) -> io::Result<TcpListener> {
        init();
        // SAFETY: обычные вызовы WinSock с проверкой результата; сокет закрывается при ошибке.
        unsafe {
            let socket = WSASocketW(
                AF_INET as i32,
                SOCK_STREAM,
                IPPROTO_TCP,
                std::ptr::null(),
                0,
                WSA_FLAG_OVERLAPPED | WSA_FLAG_NO_HANDLE_INHERIT,
            );
            if socket == INVALID_SOCKET {
                return Err(last_error("WSASocket"));
            }
            let on: i32 = 1;
            if setsockopt(socket, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, (&on as *const i32).cast::<u8>(), 4) != 0 {
                let e = last_error("setsockopt(SO_EXCLUSIVEADDRUSE)");
                closesocket(socket);
                return Err(e);
            }
            let address = SOCKADDR_IN {
                sin_family: AF_INET,
                sin_port: port.to_be(),
                sin_addr: IN_ADDR {
                    S_un: IN_ADDR_0 {
                        S_addr: u32::from(std::net::Ipv4Addr::LOCALHOST).to_be(),
                    },
                },
                sin_zero: [0; 8],
            };
            if ws_bind(
                socket,
                (&address as *const SOCKADDR_IN).cast::<SOCKADDR>(),
                std::mem::size_of::<SOCKADDR_IN>() as i32,
            ) != 0
            {
                let e = io::Error::from_raw_os_error(WSAGetLastError());
                closesocket(socket);
                return Err(e);
            }
            if ws_listen(socket, 512) != 0 {
                let e = io::Error::from_raw_os_error(WSAGetLastError());
                closesocket(socket);
                return Err(e);
            }
            Ok(TcpListener::from_raw_socket(socket as u64))
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn descriptions_are_parsed_like_dotnet() {
        assert_eq!(parse(Some("fd:7")), Some(Described::Fd(7)));
        assert_eq!(parse(Some("fd:-1")), None);
        assert_eq!(parse(Some("fd:x")), None);
        assert_eq!(parse(Some("")), None);
        assert_eq!(parse(None), None);
        assert_eq!(parse(Some("tcp:1")), None);
        let info: Vec<u8> = (0..PROTOCOL_INFO_SIZE).map(|i| (i % 251) as u8).collect();
        let text = format_wsa(&info).unwrap();
        assert!(text.starts_with("wsa:"));
        assert_eq!(parse(Some(&text)), Some(Described::ProtocolInfo(info)));
        assert_eq!(parse(Some("wsa:AAAA")), None);
        assert_eq!(parse(Some("wsa:!!!!")), None);
        assert!(format_wsa(&[1, 2, 3]).is_err());
        assert_eq!(format_fd(12), "fd:12");
    }

    #[test]
    fn base64_matches_convert() {
        assert_eq!(base64_encode(b""), "");
        assert_eq!(base64_encode(b"f"), "Zg==");
        assert_eq!(base64_encode(b"fo"), "Zm8=");
        assert_eq!(base64_encode(b"foo"), "Zm9v");
        assert_eq!(base64_encode(&[0xfb, 0xff]), "+/8=");
        for text in ["", "f", "fo", "foo", "foob", "fooba", "foobar"] {
            assert_eq!(base64_decode(&base64_encode(text.as_bytes())).unwrap(), text.as_bytes());
        }
        assert!(base64_decode("Zg=").is_none());
        assert!(base64_decode("Zg==Zg==").is_none());
    }

    #[cfg(unix)]
    #[test]
    fn the_listening_socket_is_inheritable_only_on_request() {
        let listener = bind(0).unwrap();
        let fd = raw(&listener);
        assert!(!is_inheritable(fd));
        Handoff::UnixInherit.prepare(&listener).unwrap();
        assert!(is_inheritable(fd));
        assert_eq!(
            Handoff::UnixInherit.worker_arguments(&listener),
            vec!["--listen-fd".to_string(), fd.to_string()]
        );
        assert_eq!(
            Handoff::UnixMessage.worker_arguments(&listener),
            vec!["--listen-handoff".to_string()]
        );
        assert_eq!(Handoff::UnixMessage.export(&listener, 1).unwrap(), Some(format!("fd:{fd}")));
        assert_eq!(Handoff::UnixInherit.export(&listener, 1).unwrap(), None);
        set_inheritable(fd, false).unwrap();
        assert!(!is_inheritable(fd));
    }
}
