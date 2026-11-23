// 灵枢桌面版（原生）—— 后端服务生命周期（stdio 管道版）
//
// 责任：把内嵌 Python 以 **--stdio 模式**拉起来、把 stdin/stdout 交给 API 客户端、
// 退出时收掉整棵进程树。
//
// 关键点：**不监听任何 TCP 端口**。之前用 uvicorn 起 127.0.0.1:8010，本质还是
// 「后台跑了个 web 服务」；现在请求从 stdin 递进去、响应从 stdout 读回来，
// 系统里没有 socket 在听，符合「像微信/QQ 那样双击就开」的要求。
//
// 进程树收口：Python 会 spawn 子进程（沙箱、浏览器等），只 Kill 父进程会留下
// 孤儿。这里用 Job Object 把整棵树绑在一起，exe 退出时由系统一并清理。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SpiritScout.Native
{
    /// <summary>服务启动失败时抛这个，消息直接给用户看。</summary>
    public sealed class ServiceException : Exception
    {
        public ServiceException(string message) : base(message) { }
    }

    public sealed class BackendService : IDisposable
    {
        private Process _proc;
        private IntPtr _job;
        private readonly List<string> _logLines = new List<string>();
        private readonly object _logLock = new object();

        public string PythonPath { get; private set; }
        public string WorkDir { get; private set; }

        /// <summary>客户端用的两根管子（后端起来之后才有）。</summary>
        public Stream StdinStream { get; private set; }
        public Stream StdoutStream { get; private set; }

        /// <summary>有新日志时触发（UI 可显示在启动页上）。</summary>
        public event Action<string> Log;

        public string TailLog(int lines = 20)
        {
            lock (_logLock)
            {
                int from = Math.Max(0, _logLines.Count - lines);
                return string.Join(Environment.NewLine,
                    _logLines.GetRange(from, _logLines.Count - from).ToArray());
            }
        }

        private void Append(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (_logLock)
            {
                _logLines.Add(line);
                if (_logLines.Count > 2000) _logLines.RemoveRange(0, 500);
            }
            var handler = Log;
            if (handler != null) handler(line);
        }

        /// <summary>找可用的 Python：包内 py\python.exe → .venv → PATH。</summary>
        public string LocatePython(string baseDir)
        {
            string bundled = Path.Combine(baseDir, "py", "python.exe");
            if (File.Exists(bundled)) return bundled;

            string bundledW = Path.Combine(baseDir, "py", "pythonw.exe");
            if (File.Exists(bundledW)) return bundledW;

            string venv = Path.Combine(baseDir, ".venv", "Scripts", "python.exe");
            if (File.Exists(venv)) return venv;

            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    string cand = Path.Combine(dir.Trim(), "python.exe");
                    if (File.Exists(cand)) return cand;
                }
                catch { }
            }
            throw new ServiceException(
                "找不到 Python 运行时。单文件版的 exe 首次启动会自动解压，若仍失败请把 " +
                "`py\\python.exe` 放回程序目录，或安装 Python 3.11 后重试。");
        }

        /// <summary>
        /// 启动后端（--stdio），等它打出「后端已就绪」或能被 ping 通。
        /// </summary>
        public void Start(string baseDir, int readyTimeoutSeconds = 120)
        {
            PythonPath = LocatePython(baseDir);
            WorkDir = baseDir;
            Append("[启动] Python: " + PythonPath);

            var psi = new ProcessStartInfo
            {
                FileName = PythonPath,
                // --stdio：走管道、不开端口
                Arguments = "-X faulthandler -X utf8 -m spiritscout --config configs\\system.yaml --stdio",
                WorkingDirectory = baseDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

            try
            {
                _proc = Process.Start(psi);
            }
            catch (Exception ex)
            {
                throw new ServiceException("启动 Python 失败：" + ex.Message);
            }
            if (_proc == null) throw new ServiceException("启动 Python 失败：进程没起来。");

            BindToJob(_proc);

            // stdout 是二进制帧流：设成不缓冲，否则会卡在缓冲里等满
            _proc.StandardOutput.BaseStream.Flush();
            StdinStream = _proc.StandardInput.BaseStream;
            StdoutStream = _proc.StandardOutput.BaseStream;

            // stderr 是给人看的日志
            _proc.ErrorDataReceived += (s, e) => { if (e.Data != null) Append(e.Data); };
            _proc.BeginErrorReadLine();

            var probe = new SpiritApi(StdinStream, StdoutStream);
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(readyTimeoutSeconds);
                while (DateTime.UtcNow < deadline)
                {
                    if (_proc.HasExited)
                        throw new ServiceException(
                            "后端进程启动后立即退出（退出码 " + _proc.ExitCode + "）。\n\n最近日志：\n"
                            + TailLog(15));
                    if (probe.Ping(3000))
                    {
                        Append("[就绪] 后端已通过管道应答");
                        return;
                    }
                    Thread.Sleep(400);
                }
                throw new ServiceException(
                    "等待后端就绪超时（" + readyTimeoutSeconds + " 秒）。\n\n最近日志：\n" + TailLog(15));
            }
            finally
            {
                // probe 只是借同一对流过；它的 Dispose 不关流，这里直接释放
                probe.Dispose();
            }
        }

        // ---------- Job Object ----------

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr a, string lpName);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoType,
            IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        private void BindToJob(Process proc)
        {
            try
            {
                if (_job == IntPtr.Zero)
                    _job = CreateJobObject(IntPtr.Zero, null);
                if (_job == IntPtr.Zero) return;

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                int len = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                IntPtr ptr = Marshal.AllocHGlobal(len);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ptr, (uint)len);
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
                AssignProcessToJobObject(_job, proc.Handle);
            }
            catch
            {
                // Job Object 不可用时退化成只 Kill 父进程
            }
        }

        public void Stop()
        {
            var proc = _proc;
            if (proc != null)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        // 先关 stdin：后端看到 EOF 会自己退出，比直接 Kill 干净
                        try { proc.StandardInput.Close(); } catch { }
                        if (!proc.WaitForExit(3000))
                        {
                            proc.Kill();
                            proc.WaitForExit(3000);
                        }
                    }
                }
                catch { }
            }
            if (_job != IntPtr.Zero)
            {
                try { CloseHandle(_job); } catch { }
                _job = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            Stop();
            if (_proc != null)
            {
                try { _proc.Dispose(); } catch { }
                _proc = null;
            }
        }
    }
}
