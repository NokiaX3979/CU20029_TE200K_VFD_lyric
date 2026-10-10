// PipeBroadcaster.cs  （C# 7.3 兼容）
using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public class PipeBroadcaster : IDisposable
{
    readonly string _pipeName;
    readonly Thread _serverThread;
    readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
    volatile bool _running = true;

    public PipeBroadcaster(string pipeName)
    {
        _pipeName = pipeName;
        _serverThread = new Thread(ServerLoop) { IsBackground = true };
        _serverThread.Start();
    }

    // 主程序调用：同时写控制台和广播
    public void BroadcastAndConsole(string line)
    {
        try
        {
            Console.WriteLine(line);
        }
        catch { /* ignore console errors */ }

        Broadcast(line);
    }

    // 只广播到命名管道，不写控制台（由调用方自行决定是否输出到控制台）
    public void Broadcast(string line)
    {
        _queue.Enqueue(line + "\n"); // 保留换行，便于 ReadLine
    }

    void ServerLoop()
    {
        while (_running)
        {
            NamedPipeServerStream server = null;
            try
            {
                server = new NamedPipeServerStream(_pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                // 等待客户端连接（阻塞），但可被 Dispose 中断
                server.WaitForConnection();

                using (var sw = new StreamWriter(server, Encoding.UTF8) { AutoFlush = true })
                {
                    // 先把队列中已有的发掉
                    string item;
                    while (_queue.TryDequeue(out item))
                    {
                        sw.Write(item);
                    }

                    // 然后持续发送新消息直到断开或停止
                    while (_running && server.IsConnected)
                    {
                        // 尝试取一条消息，若没有则短暂等待
                        if (_queue.TryDequeue(out item))
                        {
                            sw.Write(item);
                        }
                        else
                        {
                            Thread.Sleep(100);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 忽略单次错误，短暂等待后重试 Accept
                try { Thread.Sleep(200); } catch { }
            }
            finally
            {
                if (server != null)
                {
                    try { server.Dispose(); } catch { }
                }
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        // 触发退出：创建一个临时客户端连接以解除 WaitForConnection（可选）
        try
        {
            using (var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out))
            {
                client.Connect(100);
            }
        }
        catch { }
    }
}
