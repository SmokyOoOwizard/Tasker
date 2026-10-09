//! Бинарник `tasker`: консоль на Windows переключается в UTF-8 (`ConsoleSetup`), строка выполняется [`tasker_cli::app::run`].
use std::io::Write;

fn main() {
    let _console = tasker_cli::terminal::ConsoleSetup::open();
    let args: Vec<String> = std::env::args().skip(1).collect();
    let mut out = std::io::stdout().lock();
    let mut err = std::io::stderr().lock();
    let code = tasker_cli::app::run(&args, &mut out, &mut err);
    let _ = out.flush();
    let _ = err.flush();
    drop(out);
    drop(err);
    std::process::exit(code);
}
