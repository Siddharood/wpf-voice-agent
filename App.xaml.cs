using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace WpfVoiceAgent
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            DispatcherUnhandledException +=
                App_DispatcherUnhandledException;

            AppDomain.CurrentDomain.UnhandledException +=
                CurrentDomain_UnhandledException;

            TaskScheduler.UnobservedTaskException +=
                TaskScheduler_UnobservedTaskException;

            Exit += App_Exit;

            Debug.WriteLine("=== APPLICATION STARTED ===");
        }

        private void App_DispatcherUnhandledException(
            object sender,
            DispatcherUnhandledExceptionEventArgs e)
        {
            Debug.WriteLine(
                "=== DISPATCHER UNHANDLED EXCEPTION ===");

            Debug.WriteLine(e.Exception.ToString());

            Debug.WriteLine(
                "INNER: " +
                (e.Exception.InnerException == null
                    ? "<none>"
                    : e.Exception.InnerException.ToString()));

            // Do NOT mark it handled yet.
            // We want the debugger to show the real failure.
        }

        private void CurrentDomain_UnhandledException(
            object sender,
            UnhandledExceptionEventArgs e)
        {
            Debug.WriteLine(
                "=== APPDOMAIN UNHANDLED EXCEPTION ===");

            Exception exception =
                e.ExceptionObject as Exception;

            if (exception != null)
            {
                Debug.WriteLine(exception.ToString());
            }
            else
            {
                Debug.WriteLine(
                    "Exception object: " +
                    e.ExceptionObject);
            }

            Debug.WriteLine(
                "Runtime terminating: " +
                e.IsTerminating);
        }

        private void TaskScheduler_UnobservedTaskException(
            object sender,
            UnobservedTaskExceptionEventArgs e)
        {
            Debug.WriteLine(
                "=== UNOBSERVED TASK EXCEPTION ===");

            Debug.WriteLine(e.Exception.ToString());

            e.SetObserved();
        }

        private void App_Exit(
            object sender,
            ExitEventArgs e)
        {
            Debug.WriteLine(
                "=== APPLICATION EXIT ===");

            Debug.WriteLine(
                "EXIT CODE: " +
                e.ApplicationExitCode);
        }
    }
}