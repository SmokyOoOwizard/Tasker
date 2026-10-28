using Tasker.Stress;

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(StressOptions.Usage);
    return 0;
}

StressOptions options;
try
{
    options = StressOptions.Parse(args);
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    Console.Error.WriteLine(StressOptions.Usage);
    return 2;
}

var report = await StressRunner.Run(options, text => Console.Error.WriteLine(text));
Console.WriteLine(report.ToText());
return report.Passed ? 0 : 1;
