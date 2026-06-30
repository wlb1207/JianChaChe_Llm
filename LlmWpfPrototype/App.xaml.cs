using System.IO;
using System.Text;
using System.Windows;
using LlmWpfPrototype.Services;

namespace LlmWpfPrototype;

public partial class App : System.Windows.Application
{
    private static readonly string LogDirectory = AppPaths.LogsDirectory;
    private static readonly string StartupLogPath = Path.Combine(LogDirectory, "startup.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        Directory.CreateDirectory(LogDirectory);
        WriteStartupInfo("应用启动开始。");

        DispatcherUnhandledException += (_, args) =>
        {
            WriteStartupLog("未处理的界面异常", args.Exception);
            System.Windows.MessageBox.Show(
                $"程序启动或运行时发生异常。\n\n{args.Exception.Message}\n\n详细日志：{StartupLogPath}",
                "桥梁检查车智能设计",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
            Shutdown(-1);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                WriteStartupLog("未处理的应用域异常", exception);
            }
        };

        base.OnStartup(e);

        try
        {
            WriteStartupInfo("开始创建主窗口。");
            var window = new MainWindow();
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
            WriteStartupInfo("主窗口已显示。");
        }
        catch (Exception ex)
        {
            WriteStartupLog("主窗口创建失败", ex);
            System.Windows.MessageBox.Show(
                $"主窗口创建失败。\n\n{ex.Message}\n\n详细日志：{StartupLogPath}",
                "桥梁检查车智能设计",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    public static void WriteStartupInfo(string message)
    {
        var builder = new StringBuilder();
        builder.Append('[')
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
            .AppendLine($"] {message}")
            .AppendLine();

        File.AppendAllText(StartupLogPath, builder.ToString(), Encoding.UTF8);
    }

    public static void WriteStartupLog(string title, Exception exception)
    {
        var builder = new StringBuilder();
        builder.Append('[')
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
            .AppendLine($"] {title}")
            .AppendLine(exception.ToString())
            .AppendLine();

        File.AppendAllText(StartupLogPath, builder.ToString(), Encoding.UTF8);
    }
}
