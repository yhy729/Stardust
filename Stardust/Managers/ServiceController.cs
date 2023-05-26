﻿using System.Diagnostics;
using NewLife;
using NewLife.Log;
using NewLife.Threading;
using Stardust.Deployment;
using Stardust.Models;
using Stardust.Services;
#if !NET40
using TaskEx = System.Threading.Tasks.Task;
#endif

namespace Stardust.Managers;

/// <summary>
/// 应用服务控制器
/// </summary>
internal class ServiceController : DisposeBase
{
    #region 属性
    static Int32 _gid = 0;
    private readonly Int32 _id = Interlocked.Increment(ref _gid);
    /// <summary>编号</summary>
    public Int32 Id => _id;

    /// <summary>服务名</summary>
    public String Name { get; set; }

    /// <summary>进程ID</summary>
    public Int32 ProcessId { get; set; }

    /// <summary>进程名</summary>
    public String ProcessName { get; set; }

    /// <summary>服务信息</summary>
    public ServiceInfo Info { get; private set; }

    /// <summary>部署信息</summary>
    public DeployInfo DeployInfo { get; set; }

    /// <summary>进程</summary>
    public Process Process { get; set; }

    /// <summary>是否正在工作</summary>
    public Boolean Running { get; set; }

    /// <summary>监视文件改变的周期。默认5000ms</summary>
    public Int32 MonitorPeriod { get; set; } = 5000;

    /// <summary>延迟时间。重启进程或服务的延迟时间，默认3000ms</summary>
    public Int32 Delay { get; set; } = 3000;

    /// <summary>启动应用时的等待时间。如果该时间内进程退出，则认为启动失败</summary>
    public Int32 StartWait { get; set; } = 3000;

    /// <summary>开始时间</summary>
    public DateTime StartTime { get; set; }

    /// <summary>最大失败数。超过该数时，不再尝试启动，默认20</summary>
    public Int32 MaxFails { get; set; } = 20;

    /// <summary>事件客户端</summary>
    public IEventProvider EventProvider { get; set; }

    private String _fileName;
    private String _workdir;
    private TimerX _timer;
    private Int32 _error;
    private AppInfo _appInfo;
    #endregion

    #region 构造
    protected override void Dispose(Boolean disposing)
    {
        base.Dispose(disposing);

        _timer.TryDispose();
    }
    #endregion

    #region 方法
    /// <summary>检查并启动应用，等待一会确认进程已启动</summary>
    /// <returns>本次是否成功启动，原来已启动返回false</returns>
    public Boolean Start()
    {
        if (Running) return false;

        // 加锁避免多线程同时启动服务
        lock (this)
        {
            if (Running) return false;

            var service = Info;
            if (service == null) return false;

            // 连续错误一定数量后，不再尝试启动
            if (_error >= MaxFails)
            {
                if (_error == MaxFails) WriteLog("应用[{0}]累计错误次数{1}达到最大值{2}", Name, _error, MaxFails);

                _error++;
                return false;
            }
            _error++;

            // 修正路径
            var workDir = service.WorkingDirectory;
            var file = service.FileName?.Trim();
            if (file.IsNullOrEmpty())
            {
                WriteLog("应用[{0}]文件名为空", Name);
                return false;
            }

            if (file.Contains('/') || file.Contains('\\'))
            {
                file = file.GetFullPath();
                if (workDir.IsNullOrEmpty()) workDir = Path.GetDirectoryName(file);
            }
            _fileName = null;
            _workdir = workDir;

            var args = service.Arguments?.Trim();
            WriteLog("启动应用：{0} {1} workDir={2} Mode={3} Times={4}", file, args, workDir, service.Mode, _error);
            if (service.MaxMemory > 0) WriteLog("内存限制：{0:n0}M", service.MaxMemory);

            using var span = Tracer?.NewSpan("StartService", service);
            try
            {
                Process p;
                var isZip = file.EqualIgnoreCase("ZipDeploy") || file.EndsWithIgnoreCase(".zip");

                // 工作模式
                switch (service.Mode)
                {
                    case ServiceModes.Default:
                    case ServiceModes.Multiple:
                        break;
                    case ServiceModes.Extract:
                        WriteLog("解压后不运行，外部主机（如IIS）将托管应用");
                        Extract(file, args, workDir, false);
                        Running = true;
                        return true;
                    case ServiceModes.ExtractAndRun:
                        WriteLog("解压后在工作目录运行");
                        var deploy = Extract(file, args, workDir, true);
                        if (deploy == null || deploy.ExecuteFile.IsNullOrEmpty()) throw new Exception("无法找到启动文件");

                        file = deploy.ExecuteFile;
                        args = deploy.Arguments;
                        _fileName = deploy.ExecuteFile;
                        isZip = false;
                        break;
                    case ServiceModes.RunOnce:
                        //service.Enable = false;
                        break;
                    default:
                        break;
                }

                if (isZip)
                {
                    var deploy = new ZipDeploy
                    {
                        FileName = file,
                        WorkingDirectory = workDir,
                        UserName = service.UserName,
                        Overwrite = DeployInfo?.Overwrite,

                        Tracer = Tracer,
                        Log = new ActionLog(WriteLog),
                    };

                    // 如果出现超过一次的重启，则打开调试模式，截取控制台输出到日志
                    if (_error > 1) deploy.Debug = true;

                    if (!args.IsNullOrEmpty() && !deploy.Parse(args.Split(" "))) return false;

                    if (!deploy.Execute(StartWait))
                    {
                        WriteLog("Zip包启动失败！ExitCode={0}", deploy.Process?.ExitCode);

                        // 上报最后错误
                        if (!deploy.LastError.IsNullOrEmpty()) EventProvider?.WriteErrorEvent("ServiceController", deploy.LastError);

                        return false;
                    }

                    _fileName = deploy.ExecuteFile;

                    p = deploy.Process;
                }
                else
                {
                    //WriteLog("拉起进程：{0} {1}", file, args);
                    var si = new ProcessStartInfo
                    {
                        FileName = file,
                        Arguments = args,
                        WorkingDirectory = workDir,

                        // false时目前控制台合并到当前控制台，一起退出；
                        // true时目标控制台独立窗口，不会一起退出；
                        UseShellExecute = false,
                    };

                    // 指定用户时，以特定用户启动进程
                    if (!service.UserName.IsNullOrEmpty())
                    {
                        si.UserName = service.UserName;
                        //si.UseShellExecute = false;

                        // 在Linux系统中，改变目录所属用户
                        if (Runtime.Linux)
                        {
                            var user = service.UserName;
                            if (!user.Contains(':')) user = $"{user}:{user}";
                            Process.Start("chown", $"-R {user} {si.WorkingDirectory}");
                            Process.Start("chown", $"{user} {si.WorkingDirectory.CombinePath("../").GetBasePath()}");
                        }
                    }

                    // 如果出现超过一次的重启，则打开调试模式，截取控制台输出到日志
                    if (_error > 1)
                    {
                        // UseShellExecute 必须 false，以便于后续重定向输出流
                        si.UseShellExecute = false;
                        si.RedirectStandardError = true;
                        si.RedirectStandardOutput = true;
                    }

                    // 在环境变量中设置BasePath
                    if (si.UseShellExecute)
                        Environment.SetEnvironmentVariable("BasePath", si.WorkingDirectory);
                    else
                        si.EnvironmentVariables.Add("BasePath", si.WorkingDirectory);

                    WriteLog("工作目录: {0}", si.WorkingDirectory);
                    WriteLog("启动文件: {0}", si.FileName);
                    WriteLog("启动参数: {0}", si.Arguments);
                    if (!si.UserName.IsNullOrEmpty())
                        WriteLog("启动用户：{0}", si.UserName);

                    p = Process.Start(si);
                    if (StartWait > 0 && p.WaitForExit(StartWait) && p.ExitCode != 0)
                    {
                        WriteLog("启动失败！ExitCode={0}", p.ExitCode);

                        if (si.RedirectStandardError)
                        {
                            var rs = p.StandardOutput.ReadToEnd();
                            if (!rs.IsNullOrEmpty()) WriteLog(rs);

                            rs = p.StandardError.ReadToEnd();
                            if (!rs.IsNullOrEmpty()) WriteLog(rs);
                        }

                        return false;
                    }

                    _fileName ??= file;
                }

                if (p == null) return false;

                WriteLog("启动成功 PID={0}/{1}", p.Id, p.ProcessName);

                if (service.Mode == ServiceModes.RunOnce)
                {
                    WriteLog("单次运行完成，禁用该应用服务");
                    service.Enable = false;
                    Running = false;

                    return true;
                }

                // 记录进程信息，避免宿主重启后无法继续管理
                SetProcess(p);
                Running = true;

                StartTime = DateTime.Now;

                // 定时检查文件是否有改变
                StartMonitor();

                // 此时还不能清零，因为进程可能不稳定，待定时器检测可靠后清零
                //_error = 0;

                return true;
            }
            catch (Exception ex)
            {
                span?.SetError(ex, null);
                Log?.Write(LogLevel.Error, "{0}", ex);
                EventProvider?.WriteErrorEvent("ServiceController", ex.ToString());
            }

            return false;
        }
    }

    public ZipDeploy Extract(String file, String args, String workDir, Boolean needRun)
    {
        var isZip = file.EqualIgnoreCase("ZipDeploy") || file.EndsWithIgnoreCase(".zip");
        if (!isZip) return null;

        var deploy = new ZipDeploy
        {
            FileName = file,
            WorkingDirectory = workDir,
            Overwrite = DeployInfo?.Overwrite,

            Tracer = Tracer,
            Log = new ActionLog(WriteLog),
        };

        //var args = service.Arguments?.Trim();
        if (!args.IsNullOrEmpty() && !deploy.Parse(args.Split(" "))) return null;

        deploy.Extract(workDir);

        if (!needRun) return deploy;

        var runfile = deploy.FindExeFile(workDir);
        if (runfile == null)
        {
            WriteLog("无法找到名为[{0}]的可执行文件", deploy.FileName);
            return null;
        }

        deploy.ExecuteFile = runfile.FullName;

        return deploy;
    }

    /// <summary>停止应用，等待一会确认进程已退出</summary>
    /// <param name="reason"></param>
    public void Stop(String reason)
    {
        Running = false;

        var p = Process;
        SetProcess(null);
        if (p == null) return;

        WriteLog("停止应用 PID={0}/{1} 原因：{2}", p.Id, p.ProcessName, reason);

        using var span = Tracer?.NewSpan("StopService", $"{Info.Name} reason={reason}");
        _timer.TryDispose();
        _timer = null;

        try
        {
            if (!p.HasExited && p.CloseMainWindow())
            {
                WriteLog("已发送关闭窗口消息，等待目标进程退出");

                for (var i = 0; i < 50 && !p.HasExited; i++)
                {
                    Thread.Sleep(200);
                }
            }
        }
        catch { }

        // 优雅关闭进程
        if (!p.HasExited)
        {
            try
            {
                WriteLog("优雅退出进程：PID={0}/{1}，最大等待{2}毫秒", p.Id, p.ProcessName, 50 * 200);
                if (Runtime.Linux)
                {
                    Process.Start("kill", p.Id.ToString());

                    for (var i = 0; i < 50 && !p.HasExited; i++)
                    {
                        Thread.Sleep(200);
                    }
                }
                else if (Runtime.Windows)
                {
                    Process.Start("taskkill", $"-pid {p.Id}");

                    for (var i = 0; i < 50 && !p.HasExited; i++)
                    {
                        Thread.Sleep(200);
                    }
                }
            }
            catch { }
        }

        try
        {
            if (!p.HasExited)
            {
                WriteLog("强行结束进程 PID={0}/{1}", p.Id, p.ProcessName);
                p.Kill();
            }

            if (p.HasExited) WriteLog("进程[PID={0}]已退出！ExitCode={1}", p.Id, p.ExitCode);
        }
        catch (Exception ex)
        {
            WriteLog("进程[PID={0}]退出失败！{1}", p.Id, ex.Message);
            span?.SetError(ex, null);
        }
    }

    /// <summary>设置服务信息</summary>
    /// <param name="info"></param>
    public void SetInfo(ServiceInfo info)
    {
        if (Info != info)
        {
            Info = info;
            _error = 0;
        }
    }

    /// <summary>检查已存在进程并接管，如果进程已退出则重启</summary>
    /// <returns>本次是否成功启动（或接管），原来已启动返回false</returns>
    public Boolean Check()
    {
        using var span = Tracer?.NewSpan("CheckService", Info.Name);

        // 获取当前进程Id
        var mypid = Process.GetCurrentProcess().Id;

        // 进程存在，常规判断内存
        var p = Process;
        if (p != null)
        {
            span?.AppendTag("CheckMaxMemory");
            try
            {
                if (!p.HasExited)
                {
                    _error = 0;

                    // 检查内存限制
                    if (Info.MaxMemory <= 0) return false;

                    var mem = p.WorkingSet64 / 1024 / 1024;
                    if (mem <= Info.MaxMemory) return true;

                    WriteLog("内存超限！{0}>{1}", mem, Info.MaxMemory);

                    Stop("内存超限");
                }
                else
                {
                    WriteLog("应用[{0}/{1}]已退出！", p.Id, Name);
                }

                p = null;
                Process = null;
                // 这里不能清空 ProcessId 和 ProcessName，可能因为异常操作导致进程丢了，但是根据名称还能找到。也可能外部启动了进程
                //SetProcess(null);

                Running = false;
            }
            catch (Exception ex)
            {
                span?.SetError(ex, null);
            }
        }

        // 进程不存在，但Id存在
        if (p == null && ProcessId > 0 && ProcessId != mypid)
        {
            span?.AppendTag($"GetProcessById({ProcessId})");
            try
            {
                p = Process.GetProcessById(ProcessId);

                var exited = false;
                try
                {
                    exited = p.HasExited;
                }
                catch { }

                // 这里的进程名可能是 dotnet/java，照样可以使用
                if (p != null && !exited && p.ProcessName == ProcessName) return TakeOver(p, $"按[Id={ProcessId}]查找");
            }
            catch (Exception ex)
            {
                span?.SetError(ex, null);

                if (ex is not ArgumentException)
                {
                    Log?.Error("{0}", ex);
                    EventProvider?.WriteErrorEvent("ServiceController", ex.ToString());
                }
            }

            p = null;
            ProcessId = 0;
        }

        // 进程不存在，但名称存在
        if (p == null && !ProcessName.IsNullOrEmpty() && Info.Mode != ServiceModes.Multiple)
        {
            if (ProcessName.EqualIgnoreCase("dotnet", "java"))
            {
                var target = _fileName ?? Info.FileName;
                if (target.EqualIgnoreCase("dotnet", "java"))
                {
                    var ss = Info.Arguments.Split(' ');
                    if (ss != null) target = ss.FirstOrDefault(e => e.EndsWithIgnoreCase(".dll", ".jar"));
                }
                if (!target.IsNullOrEmpty())
                {
                    //target = Path.GetFileName(target);
                    span?.AppendTag($"GetProcessesByFile({target}) ProcessName={ProcessName}");

                    // 遍历所有进程，从命令行参数中找到启动文件名一致的进程
                    foreach (var item in Process.GetProcesses())
                    {
                        if (item.Id == mypid) continue;
                        if (!item.ProcessName.EqualIgnoreCase(ProcessName)) continue;

                        var name = AppInfo.GetProcessName(item);
                        if (!name.IsNullOrEmpty())
                        {
                            span?.AppendTag($"id={item.Id} name={name}");

                            //name = Path.GetFileName(name);
                            if (name.EqualIgnoreCase(target)) return TakeOver(item, $"按[{ProcessName} {target}]查找");
                        }
                    }
                }
            }
            else
            {
                span?.AppendTag($"GetProcessesByName({ProcessName})");

                var ps = Process.GetProcessesByName(ProcessName).Where(e => e.Id != mypid).ToArray();
                if (ps.Length > 0) return TakeOver(ps[0], $"按[Name={ProcessName}]查找");
            }
        }

        // 准备启动进程
        var rs = Start();

        // 检测并上报性能
        p = Process;
        if (p != null && EventProvider is StarClient client)
        {
            if (_appInfo == null)
                _appInfo = new AppInfo(p) { AppName = Info.Name };
            else
                _appInfo.Refresh();

            TaskEx.Run(() => client.AppPing(_appInfo));
        }

        return rs;
    }

    Boolean TakeOver(Process p, String reason)
    {
        using var span = Tracer?.NewSpan(nameof(TakeOver), new { p.Id, p.ProcessName, reason });

        WriteLog("应用[{0}/{1}]已启动（{2}），直接接管", p.Id, Name, reason);

        SetProcess(p);
        if (Info != null) StartMonitor();

        if (StartTime.Year < 2000) StartTime = DateTime.Now;

        Running = true;

        return true;
    }

    public void SetProcess(Process process)
    {
        Process = process;
        if (process != null)
        {
            ProcessId = process.Id;
            ProcessName = process.ProcessName;
        }
        else
        {
            ProcessId = 0;
            ProcessName = null;
            _appInfo = null;
        }
    }

    /// <summary>获取进程信息</summary>
    /// <returns></returns>
    public ProcessInfo ToModel()
    {
        return new ProcessInfo
        {
            Name = Name,
            ProcessId = ProcessId,
            ProcessName = ProcessName,
            CreateTime = StartTime,
            UpdateTime = DateTime.Now,
        };
    }

    private void StartMonitor()
    {
        // 定时检查文件是否有改变
        _timer ??= new TimerX(MonitorFileChange, null, 1_000, MonitorPeriod) { Async = true };
    }

    private readonly Dictionary<String, DateTime> _files = new();

    /// <summary>是否已准备。发生文件变化时，进入就绪状态，持续5秒没有改变后执行重启</summary>
    private Boolean _ready;
    private DateTime _readyTime;

    private void MonitorFileChange(Object state)
    {
        var first = _files.Count == 0;
        var changed = "";

        // 检查目标目录所有 *.dll 文件
        var dir = _workdir;
        if (dir.IsNullOrEmpty()) dir = Info?.WorkingDirectory;
        if (dir.IsNullOrEmpty()) return;

        var di = dir.AsDirectory();
        if (di == null || !di.Exists) return;

        if (first) WriteLog("监视文件改变：{0}", di.FullName);

        foreach (var fi in di.GetAllFiles("*.dll;*.exe;*.zip;*.jar"))
        {
            var time = fi.LastWriteTime.Trim();
            if (_files.TryGetValue(fi.FullName, out var dt))
            {
                if (dt < time)
                {
                    _files[fi.FullName] = time;
                    changed = fi.FullName;
                }
            }
            else
            {
                _files[fi.FullName] = time;
                changed = fi.FullName;
            }
        }

        using var span = !changed.IsNullOrEmpty() || _ready ?
            Tracer?.NewSpan("ServiceFileChange", changed) :
            null;

        if (!first && !changed.IsNullOrEmpty())
        {
            var msg = $"文件[{changed}]发生改变，停止应用，延迟{Delay}毫秒后启动";
            WriteLog(msg);

            // 进入就绪状态
            if (!_ready)
            {
                Stop(msg);

                _ready = true;

                // 快速再次检查
                _timer?.SetNext(1000);
            }

            // 更新最后就绪时间，该时间之后5秒再启动
            _readyTime = DateTime.Now;
        }

        if (_ready && _readyTime.AddMilliseconds(Delay) < DateTime.Now)
        {
            Start();

            _ready = false;
        }
    }
    #endregion

    #region 日志
    /// <summary>性能追踪</summary>
    public ITracer Tracer { get; set; }

    /// <summary>日志</summary>
    public ILog Log { get; set; }

    /// <summary>写日志</summary>
    /// <param name="format"></param>
    /// <param name="args"></param>
    public void WriteLog(String format, params Object[] args)
    {
        Log?.Info($"[{Id}/{Name}]{format}", args);

        var msg = (args == null || args.Length == 0) ? format : String.Format(format, args);
        DefaultSpan.Current?.AppendTag(msg);

        if (format.Contains("错误") || format.Contains("失败"))
            EventProvider?.WriteErrorEvent(nameof(ServiceController), msg);
        else
            EventProvider?.WriteInfoEvent(nameof(ServiceController), msg);
    }
    #endregion
}