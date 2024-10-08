using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.EventStream;

namespace PhotinoSveltekitDevServer;

public class DevServer
{
    private const string URL_PREFIX = "http://";
    private CancellationTokenSource cancelDevServer;
    private Uri devServerUrl;
    private ManualResetEvent devServerReady;

    public DevServer()
    {
        devServerUrl = null!;
        cancelDevServer = new CancellationTokenSource();
        devServerReady = new ManualResetEvent(false);
    }

    private string GetProjectDirectory()
    {
        var entry = Assembly.GetEntryAssembly();
        if (entry == null) throw new InvalidOperationException();

        return Path.GetFullPath(Path.Combine(entry.Location, "..", "..", "..", ".."));
    }

    public async Task Start()
    {
        var projDir = GetProjectDirectory();

        var stdin = new MemoryStream();

        // start a separate process with the npm command.
        var cmd = Cli.Wrap(ProgramDefaults.DevTerminalExecutable)
            .WithWorkingDirectory(Path.Combine(projDir, ProgramDefaults.DevUserInterfaceDirectory))
            .WithStandardInputPipe(PipeSource.FromStream(stdin))
            .WithArguments("/C " + ProgramDefaults.DevRunUserInterfaceDevMode);

        int? pid = null;

        try
        {
            // listen for all incoming events, especially the stdout from the dev server
            await foreach (var cmdEvent in cmd.ListenAsync(Encoding.UTF8, cancelDevServer.Token))
            {
                switch (cmdEvent)
                {
                    case StartedCommandEvent started:
                        Console.WriteLine($"Process started; ID: {started.ProcessId}");
                        pid = started.ProcessId;
                        break;
                    case StandardOutputCommandEvent stdOut:
                        Console.WriteLine($"Out> {stdOut.Text}");

                        if (stdOut.Text.Contains(URL_PREFIX))
                        {
                            // if the text contains an url, try to find the url in the message, so that we know
                            // where we should redirect the user, so that he can visit the dev server.
                            devServerUrl = DetermineDevServerUrl(stdOut);
                            devServerReady.Set();
                        }
                        break;
                    case StandardErrorCommandEvent stdErr:
                        Console.WriteLine($"Err> {stdErr.Text}");
                        break;
                    case ExitedCommandEvent exited:
                        Console.WriteLine($"Process exited; Code: {exited.ExitCode}");
                        break;
                }
            }
        } catch (OperationCanceledException)
        {
            if (pid != null)
            {
                Console.WriteLine($"Killing {pid.Value} and descendents");
                var proc = Process.GetProcessById(pid.Value);
                proc?.Kill();
            }
        }
    }

    public void WaitUntilReady()
    {
        devServerReady.WaitOne();
    }

    public void Stop()
    {
        cancelDevServer.Cancel();
    }

    public Uri GetUrl()
    {
        return devServerUrl;
    }

    private static Uri DetermineDevServerUrl(StandardOutputCommandEvent stdOut)
    {
        // this is really hacky!
        // since the text contains lots of interesting characters, such as console colorings, arrows etc
        // we need to try to extract the url from all that useless stuff. 
        // This is the best way i came up with after tons of googeling
        var withoutUnicodes = Regex.Replace(stdOut.Text, @"[^\x20-\xaf]+", ""); ;
        var withoutColorCodes = Regex.Replace(withoutUnicodes, "\\[[0-9]{1,2}m", string.Empty);
        withoutColorCodes = withoutColorCodes.Trim();
        var httpIndex = withoutColorCodes.IndexOf(URL_PREFIX);
        var host = withoutColorCodes.Substring(httpIndex);
        return new Uri(host);
    }

    private bool IsReady()
    {
        // assume the dev server is ready, once we were able to determine the dev server url.
        return devServerUrl != null;
    }
}
