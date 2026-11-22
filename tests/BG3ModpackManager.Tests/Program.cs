using BG3ModpackManager.Tests;

if (args.Length >= 3 && string.Equals(args[0], "--verify-readonly", StringComparison.OrdinalIgnoreCase))
{
    return await RealDataReadOnlyVerifier.VerifyAsync(args[1], args[2]);
}

if (args.Length >= 2 && string.Equals(args[0], "--verify-downloads", StringComparison.OrdinalIgnoreCase))
{
    return await RealDataReadOnlyVerifier.VerifyDownloadsAsync(args[1]);
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("LspkReaderTests", () => { LspkReaderTests.RunAll(); return Task.CompletedTask; }),
    ("ArchiveImportTests", ArchiveImportTests.RunAllAsync),
    ("ProfileSwitchTests", ProfileSwitchTests.RunAllAsync),
    ("LoadOrderReviewTests", LoadOrderReviewTests.RunAllAsync),
    ("GameProcessGuardTests", GameProcessGuardTests.RunAllAsync),
    ("ProfileExportTests", ProfileExportTests.RunAllAsync),
    ("LocalizationTests", () => { LocalizationTests.RunAll(); return Task.CompletedTask; })
};

int failed = 0;
foreach ((string name, Func<Task> run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"[PASS] {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"[FAIL] {name}: {ex}");
    }
}

Console.WriteLine($"Summary: {tests.Length - failed}/{tests.Length} suites passed.");
return failed == 0 ? 0 : 1;
