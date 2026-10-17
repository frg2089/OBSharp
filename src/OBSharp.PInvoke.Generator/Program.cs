namespace OBSharp.PInvoke.Generator;

internal static class Program
{
    private static int Main(string[] args)
    {
        var pipeline = new Pipeline(FindRepoRoot());
        foreach (var raw in args)
        {
            var opt = raw.TrimStart('-', '/').Replace("-", string.Empty);
            if (opt.Equals("SkipRaw", StringComparison.OrdinalIgnoreCase))
            {
                pipeline.SkipRaw = true;
            }
            else if (opt.Equals("SkipBuild", StringComparison.OrdinalIgnoreCase))
            {
                pipeline.SkipBuild = true;
            }
            else
            {
                Console.Error.WriteLine($"Unknown option: {raw}");
                Console.Error.WriteLine("Usage: dotnet run --project src/OBSharp.PInvoke.Generator [-- -SkipRaw] [-SkipBuild]");
                return 2;
            }
        }

        try
        {
            pipeline.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    // The generator may be launched from any working directory; the repo root is
    // found by walking up from the built assembly until OBSharp.slnx appears.
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OBSharp.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repo root (OBSharp.slnx) above {AppContext.BaseDirectory}");
    }
}
