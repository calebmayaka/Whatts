using System.Runtime.InteropServices;

namespace Whatts;

static class Program
{
    const string InstanceName = @"Local\Whatts.Instance";
    const string ShowEventName = @"Local\Whatts.Show";

    [STAThread]
    static void Main(string[] args)
    {
        bool tray = args.Contains("--tray", StringComparer.OrdinalIgnoreCase);

        using var instance = new Mutex(false, InstanceName, out bool isFirst);
        if (!isFirst)
        {
            // Already running: bring that copy's window up instead of opening a second one.
            if (!tray && EventWaitHandle.TryOpenExisting(ShowEventName, out var show))
            {
                using (show)
                {
                    AllowSetForegroundWindow(ASFW_ANY); // we have foreground rights; hand them over
                    show.Set();
                }
            }
            return;
        }

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ApplicationConfiguration.Initialize();
        var form = new MainForm(startHidden: tray);

        var wait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) =>
        {
            try
            {
                if (form.IsHandleCreated) form.BeginInvoke(new Action(form.ShowFromTray));
            }
            catch (InvalidOperationException)
            {
                // The form is closing; nothing to show.
            }
        }, null, Timeout.Infinite, executeOnlyOnce: false);

        Application.Run(form);
        wait.Unregister(null);
    }

    const int ASFW_ANY = -1;

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);
}
