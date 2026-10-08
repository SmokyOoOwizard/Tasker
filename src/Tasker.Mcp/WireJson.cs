using System.Buffers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace Tasker.Mcp;

/// <summary>
/// Убирает <c>\uXXXX</c> из JSON на проводе MCP. Конверт JSON-RPC кодирует SDK (ModelContextProtocol 2.2.0, последняя версия)
/// своими опциями (<c>McpJsonUtilities.DefaultOptions</c>, source-generated, не настраиваются ни через <c>ServerOptions</c>,
/// ни через параметры транспорта), и кириллица, «ёлочки» и эмодзи уходят в сеть как <c>\uXXXX</c> (в 3 и 6 раз длиннее
/// символа в UTF-8). Поэтому тело ответа пропускается через <see cref="Rewrite"/>: лексический проход по байтам,
/// без разбора и повторной сериализации JSON.
/// </summary>
/// <remarks>
/// Правила те же, что у <c>TaskerJson</c> (экранируются только <c>"</c>, <c>\</c>, управляющие символы, U+007F–U+009F,
/// U+2028/2029 и одиночные суррогаты). Все прочие <c>\uXXXX</c> заменяются самим символом в UTF-8; пара суррогатов — одним
/// символом вне BMP; <c>"</c>, <c>\</c> и управляющие с короткой формой — этой формой (<c>\"</c>, <c>\\</c>, <c>\n</c>).
/// Остальные управляющие, U+007F–U+009F, U+2028/2029 и одиночные суррогаты остаются как есть. Остальные escape-последовательности (<c>\n</c>, <c>\"</c>, <c>\\</c>) копируются как есть и
/// пропускаются целиком, поэтому <c>\\u0041</c> (обратная косая и текст «u0041») не портится. Обратная косая вне строк JSON
/// недопустима, так что заменяется только внутри строк. Одинаково верно для <c>application/json</c> и для SSE
/// (<c>text/event-stream</c>: в строке <c>data:</c> JSON без переводов строк).
/// </remarks>
public static class WireJson
{
    /// <summary>Подключает перекодирование ответов MCP-эндпоинта (JSON и SSE); для остальных типов содержимого не делает ничего.</summary>
    public static void Unescape(IEndpointConventionBuilder endpoints) =>
        endpoints.Finally(builder =>
        {
            if (builder is not RouteEndpointBuilder route || route.RequestDelegate is not { } next)
                return;

            route.RequestDelegate = async context =>
            {
                var prior = context.Features.Get<IHttpResponseBodyFeature>()!;
                // Тип содержимого к началу ответа уже выставлен, заголовки ещё не отправлены.
                // Content-Length после замены неверен; сжатое тело не трогаем.
                var stream = new RewritingStream(prior.Stream, () =>
                {
                    var type = context.Response.ContentType;
                    // SDK ставит Content-Encoding: identity (чтобы прокси не сжимали SSE) — это не сжатие.
                    var encoding = context.Response.Headers.ContentEncoding.ToString();
                    if (type == null || !(encoding.Length == 0 || encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)) ||
                        !(type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ||
                          type.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase)))
                        return false;
                    context.Response.ContentLength = null;
                    return true;
                });
                // Ответ начинается либо с StartAsync (тогда сработает OnStarting), либо с первой записи в поток (тогда решает она).
                context.Response.OnStarting(() =>
                {
                    _ = stream.Active;
                    return Task.CompletedTask;
                });
                // Подменяется именно возможность тела ответа, а не Response.Body: SDK пишет через BodyWriter (PipeWriter),
                // и запись мимо Stream обошла бы перекодирование.
                var feature = new StreamResponseBodyFeature(stream, prior);
                context.Features.Set<IHttpResponseBodyFeature>(feature);
                try
                {
                    await next(context);
                    await feature.CompleteAsync();
                    await stream.CompleteAsync();
                }
                finally
                {
                    context.Features.Set(prior);
                }
            };
        });

    /// <summary>
    /// Записывает в <paramref name="output"/> <paramref name="input"/> без лишних <c>\uXXXX</c>. Недописанный в конце
    /// escape (до 11 байт) в вывод не попадает: возвращается число байт хвоста входа, их надо дописать перед следующим куском
    /// (<paramref name="final"/> — хвоста не бывает, остаток копируется как есть).
    /// </summary>
    /// <returns>Сколько байт с конца <paramref name="input"/> остались необработанными.</returns>
    public static int Rewrite(ReadOnlySpan<byte> input, IBufferWriter<byte> output, bool final)
    {
        var i = 0;
        var n = input.Length;
        while (i < n)
        {
            // Обычные байты — пачкой до следующей косой.
            var plain = input[i..].IndexOf((byte)'\\');
            if (plain < 0)
            {
                output.Write(input[i..]);
                return 0;
            }

            if (plain > 0)
            {
                output.Write(input.Slice(i, plain));
                i += plain;
            }

            if (i + 1 >= n)
            {
                if (final)
                    break;
                return n - i;
            }

            if (input[i + 1] != 'u')
            {
                output.Write(input.Slice(i, 2));
                i += 2;
                continue;
            }

            if (i + 6 > n)
            {
                if (final)
                    break;
                return n - i;
            }

            if (!TryHex(input.Slice(i + 2, 4), out var unit))
            {
                output.Write(input.Slice(i, 2));
                i += 2;
                continue;
            }

            var scalar = -1;
            var length = 6;
            if (unit is >= 0xD800 and <= 0xDBFF)
            {
                // Первая половина пары: нужна вторая сразу следом.
                var rest = n - (i + 6);
                var mayBeLow = rest == 0 || (input[i + 6] == '\\' && (rest < 2 || input[i + 7] == 'u'));
                if (rest < 6 && mayBeLow && !final)
                    return n - i;
                if (rest >= 6 && input[i + 6] == '\\' && input[i + 7] == 'u' && TryHex(input.Slice(i + 8, 4), out var low) && low is >= 0xDC00 and <= 0xDFFF)
                {
                    scalar = char.ConvertToUtf32((char)unit, (char)low);
                    length = 12;
                }
            }
            else if (unit is not (>= 0xDC00 and <= 0xDFFF) && !NeedsEscape(unit))
                scalar = unit;

            // Кавычку и косую SDK пишет как " и \, а управляющие — как \u000A: короткая форма вдвое короче и привычнее.
            var shortForm = unit switch { '"' => '"', '\\' => '\\', '\n' => 'n', '\r' => 'r', '\t' => 't', '\b' => 'b', '\f' => 'f', _ => '\0' };
            if (length == 6 && shortForm != '\0')
            {
                var span = output.GetSpan(2);
                span[0] = (byte)'\\';
                span[1] = (byte)shortForm;
                output.Advance(2);
            }
            else if (scalar < 0)
                output.Write(input.Slice(i, length));
            else
                WriteUtf8(output, scalar);
            i += length;
        }

        if (i < n)
            output.Write(input[i..]);
        return 0;
    }

    private static bool NeedsEscape(int unit) =>
        unit < 0x20 || unit is '"' or '\\' or (>= 0x7F and < 0xA0) or 0x2028 or 0x2029;

    private static bool TryHex(ReadOnlySpan<byte> digits, out int value)
    {
        value = 0;
        foreach (var d in digits)
        {
            var v = d switch
            {
                >= (byte)'0' and <= (byte)'9' => d - '0',
                >= (byte)'a' and <= (byte)'f' => d - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => d - 'A' + 10,
                _ => -1
            };
            if (v < 0)
                return false;
            value = value << 4 | v;
        }

        return true;
    }

    private static void WriteUtf8(IBufferWriter<byte> output, int scalar)
    {
        var span = output.GetSpan(4);
        var written = new System.Text.Rune(scalar).EncodeToUtf8(span);
        output.Advance(written);
    }

    /// <summary>Поток-обёртка над телом ответа: пока <see cref="Active"/> — пропускает записи через <see cref="Rewrite"/>.</summary>
    private sealed class RewritingStream(Stream inner, Func<bool> decide) : Stream
    {
        private bool? _active;

        private readonly ArrayBufferWriter<byte> _buffer = new();
        private byte[] _carry = [];

        public bool Active => _active ??= decide();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        // Незавершённый escape на сбросе не отдаём: половина «\\», выпущенная как есть, исказила бы следующий кусок.
        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (!Active)
            {
                inner.Write(buffer);
                return;
            }

            Process(buffer, final: false);
            inner.Write(_buffer.WrittenSpan);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!Active)
            {
                await inner.WriteAsync(buffer, cancellationToken);
                return;
            }

            Process(buffer.Span, final: false);
            if (_buffer.WrittenCount > 0)
                await inner.WriteAsync(_buffer.WrittenMemory, cancellationToken);
        }

        /// <summary>Конец ответа: недописанный хвост уходит как есть.</summary>
        public async Task CompleteAsync()
        {
            if (_active != true || _carry.Length == 0)
                return;
            Process([], final: true);
            await inner.WriteAsync(_buffer.WrittenMemory);
        }

        private void Process(ReadOnlySpan<byte> chunk, bool final)
        {
            _buffer.ResetWrittenCount();
            if (_carry.Length > 0)
            {
                var joined = new byte[_carry.Length + chunk.Length];
                _carry.CopyTo(joined, 0);
                chunk.CopyTo(joined.AsSpan(_carry.Length));
                var rest = Rewrite(joined, _buffer, final);
                _carry = rest == 0 ? [] : joined[^rest..];
                return;
            }

            var tail = Rewrite(chunk, _buffer, final);
            _carry = tail == 0 ? [] : chunk[^tail..].ToArray();
        }
    }
}
