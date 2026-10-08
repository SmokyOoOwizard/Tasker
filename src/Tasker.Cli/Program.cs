Tasker.Core.PerfTrace.Mark("main");
// Windows: консоль в UTF-8 (кириллица в cmd.exe и старом PowerShell), перенаправление — UTF-8 без BOM; в остальных системах — как есть.
using var console = Tasker.Cli.ConsoleSetup.Open();
var code = await Tasker.Cli.CliApp.Run(args, console.Output, console.Error, null, null, console.Input, console.Input == null ? null : false);
Tasker.Core.PerfTrace.Mark("end");
Tasker.Core.PerfTrace.Dump(console.Error);
return code;
