using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tasker.Core;

namespace Tasker.Daemon.Host;

/// <summary>
/// Сообщение между супервизором демона и его рабочим процессом: одна строка JSON. Супервизор пишет в stdin рабочего процесса,
/// рабочий пишет в stdout строки с префиксом <see cref="WireMessage.Prefix"/> (остальной вывод игнорируется).
/// Один тип на все сообщения, лишние поля неизвестны другой стороне — так сборки супервизора и рабочего процесса
/// могут отличаться на версию (супервизор живёт дольше рабочих процессов и обновляется только перезапуском демона).
/// <list type="bullet">
/// <item>супервизор → рабочий: <c>hello</c> (секрет управления, порт), <c>activate</c> (открыть приём вызовов), <c>drain</c> (закончить начатое и выйти),
/// <c>stop</c> (остановиться), <c>workers</c> (список рабочих процессов для статуса), <c>reply</c> (ответ на запрос);</item>
/// <item>рабочий → супервизор: <c>ready</c> (области открыты, можно принимать вызовы), <c>active</c> (приём вызовов открыт),
/// <c>request</c> (<c>upgrade</c> или <c>stop</c> из управления демоном).</item>
/// </list>
/// </summary>
internal sealed class WireMessage
{
    public const string Prefix = "@tasker ";

    public string Type { get; set; } = "";

    /// <summary>Номер запроса (<c>request</c>): ответ (<c>reply</c>) несёт тот же.</summary>
    public long Id { get; set; }

    public string? Op { get; set; }
    public bool? Ok { get; set; }
    public string? Message { get; set; }

    // hello
    public string? Token { get; set; }
    public int? Port { get; set; }
    public int? SupervisorPid { get; set; }
    public DateTimeOffset? SupervisorStartedAt { get; set; }

    /// <summary>Слушающий сокет, если он передаётся сообщением, а не наследуется (Windows): см. <see cref="ListenerHandoff"/>.</summary>
    public string? ListenSocket { get; set; }

    // ready
    public string? Version { get; set; }
    public string? Build { get; set; }
    public WorkspaceStatus[]? Workspaces { get; set; }

    // workers
    public WorkerStatus[]? Workers { get; set; }

    // request upgrade / reply
    public UpgradeRequest? Upgrade { get; set; }

    /// <summary>Пути областей, которые у старого процесса открыты: новый обязан их открыть, иначе замена откатывается.</summary>
    public string[]? Require { get; set; }

    public UpgradeResult? Result { get; set; }

    private static readonly JsonSerializerOptions Json = new(TaskerJson.Options) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string ToLine() => JsonSerializer.Serialize(this, Json);

    public static WireMessage? Parse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<WireMessage>(line, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Системные вызовы Unix, которых нет в .NET: наследование слушающего сокета дочерним процессом.</summary>
internal static class UnixFd
{
    private const int GetFd = 1;
    private const int SetFd = 2;
    private const int CloseOnExec = 1;

    // fcntl — функция с переменным числом аргументов. На Apple arm64 такие аргументы передаются через стек, на остальных —
    // регистрами; значение повторено во всех позициях, поэтому читается правильно при любой конвенции.
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(int fd, int command, long a2, long a3, long a4, long a5, long a6, long a7, long a8);

    /// <summary>Дескриптор остаётся открытым в дочерних процессах (<paramref name="inheritable"/>) или закрывается при запуске программы.</summary>
    public static void SetInheritable(int fd, bool inheritable)
    {
        long value = inheritable ? 0 : CloseOnExec;
        if (Fcntl(fd, SetFd, value, value, value, value, value, value, value) != 0)
            throw new InvalidOperationException($"fcntl(F_SETFD) failed for descriptor {fd}: errno {Marshal.GetLastWin32Error()}");
    }

    public static bool IsInheritable(int fd) => Fcntl(fd, GetFd, 0, 0, 0, 0, 0, 0, 0) is var flags && flags >= 0 && (flags & CloseOnExec) == 0;
}
