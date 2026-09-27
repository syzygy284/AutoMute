using System;
using System.Threading;
using System.Windows.Forms;

namespace AutoMute
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            bool startHidden = false;
            foreach (string a in args)
            {
                if (string.Equals(a, "/minimized", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase))
                    startHidden = true;
            }

            bool firstInstance;
            using (Mutex mutex = new Mutex(true, @"Local\AutoMute_SingleInstance_5b7e2c", out firstInstance))
            {
                if (!firstInstance)
                {
                    // Two copies would fight over the mute state.
                    if (!startHidden)
                        MessageBox.Show("AutoMute is already running - look for its speaker icon in the system tray " +
                                        "(you may need to click the ^ arrow).", "AutoMute",
                                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 1;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                {
                    MessageBox.Show("Unexpected error:\n\n" + e.Exception, "AutoMute",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                };

                Application.Run(new TrayContext(startHidden));
                GC.KeepAlive(mutex);
            }
            return 0;
        }
    }
}
