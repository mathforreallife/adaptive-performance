using System.Diagnostics;
using System.Globalization;

const int Success = 0;
const int UsageError = 2;
const double PeriodMs = 20;
const int SpinIterations = 50;

int threads;
double dutyPercent;
double seconds;
try
{
    threads = (int)Number("--threads", 1, 1, 256);
    dutyPercent = Number("--duty", 50, 1, 100);
    seconds = Number("--seconds", 30, 0.1, 86_400);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine("Usage: Gapo.LoadGenerator [--threads=<1-256>] [--duty=<1-100 percent>] [--seconds=<run time>]");
    return UsageError;
}

var busyMs = PeriodMs * dutyPercent / 100.0;
var runTime = TimeSpan.FromSeconds(seconds);
var clock = Stopwatch.StartNew();
var workers = Enumerable.Range(0, threads).Select(_ => new Thread(BurnInPeriods) { IsBackground = true }).ToList();
workers.ForEach(worker => worker.Start());
workers.ForEach(worker => worker.Join());
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Load generator: {threads} thread(s) at {dutyPercent:0}% for {clock.Elapsed.TotalSeconds:0.0} s."));
return Success;

void BurnInPeriods()
{
    while (clock.Elapsed < runTime)
    {
        var periodStart = clock.Elapsed.TotalMilliseconds;
        while (clock.Elapsed.TotalMilliseconds - periodStart < busyMs)
        {
            Thread.SpinWait(SpinIterations);
        }

        var idleMs = PeriodMs - (clock.Elapsed.TotalMilliseconds - periodStart);
        if (idleMs >= 1)
        {
            Thread.Sleep((int)idleMs);
        }
    }
}

double Number(string name, double fallback, double minimum, double maximum)
{
    var prefix = name + "=";
    var text = args.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    if (text is null)
    {
        return fallback;
    }

    return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= minimum && value <= maximum
        ? value
        : throw new ArgumentException($"{name} expects a number between {minimum} and {maximum}.");
}
