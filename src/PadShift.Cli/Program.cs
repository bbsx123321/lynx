using System.Text;

namespace PadShift.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (Exception)
        {
            // Redirected consoles may not support changing the encoding.
        }

        using var handler = new HttpClientHandler();
        var runner = new CliRunner(handler, Console.Out, Console.Error);
        try
        {
            return runner.Run(args);
        }
        finally
        {
            runner.Dispose();
        }
    }
}
