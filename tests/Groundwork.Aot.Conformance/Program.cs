using Groundwork.Testing;

var factory = new InMemoryProviderFactory();
var reports = new[]
{
    (Surface: "synchronous", Report: ConformanceSuite.Run(factory, "memory://native-aot-conformance")),
    (Surface: "asynchronous", Report: await ConformanceSuite.RunAsync(factory, "memory://native-aot-conformance"))
};

foreach (var (surface, report) in reports)
{
    foreach (var failure in report.Failures)
        Console.Error.WriteLine($"{surface}: {failure.Name}: {failure.Failure}");
}

if (reports.Any(result => !result.Report.Passed))
    return 1;

Console.WriteLine($"Native AOT conformance passed: {reports.Sum(result => result.Report.Checks.Count)} checks.");
return 0;
