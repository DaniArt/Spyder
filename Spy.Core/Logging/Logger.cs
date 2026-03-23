using System;
using System.IO;
using System.Text;

namespace Spy.Core.Logging;

public static class Logger
{
    static readonly object _sync = new();
    static readonly string _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spyder", "logs");
    static readonly string _file = Path.Combine(_dir, "vcl.log");
    const long MaxSize = 5 * 1024 * 1024; // 5MB

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message) => Write("ERROR", message);

    static void Write(string level, string message)
    {
        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(_dir);
                RotateIfNeeded();
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(_file, line, Encoding.UTF8);
            }
        }
        catch { }
    }

    static void RotateIfNeeded()
    {
        try
        {
            if (File.Exists(_file))
            {
                var fi = new FileInfo(_file);
                if (fi.Length > MaxSize)
                {
                    var bak = _file + ".1";
                    if (File.Exists(bak)) File.Delete(bak);
                    File.Move(_file, bak);
                }
            }
        }
        catch { }
    }
}
