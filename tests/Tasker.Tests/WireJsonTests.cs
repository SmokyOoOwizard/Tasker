using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Tasker.Mcp;
using Xunit;

namespace Tasker.Tests;

/// <summary>Слой <see cref="WireJson"/> (TSK-95): \uXXXX на проводе MCP заменяются символами, JSON остаётся равным по смыслу.</summary>
public class WireJsonTests
{
    private static string Rewrite(string json, int chunk = int.MaxValue)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var output = new ArrayBufferWriter<byte>();
        var carry = Array.Empty<byte>();
        for (var i = 0; i < bytes.Length; i += chunk)
        {
            var piece = bytes.AsSpan(i, Math.Min(chunk, bytes.Length - i));
            var input = carry.Concat(piece.ToArray()).ToArray();
            var rest = WireJson.Rewrite(input, output, final: false);
            carry = input[^rest..];
        }

        WireJson.Rewrite(carry, output, final: true);
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    [Theory]
    [InlineData("\"\\u041f\\u0440\\u0438\\u0432\\u0435\\u0442\"", "\"Привет\"")]
    [InlineData("\"\\u00ab\\u0451\\u043b\\u043e\\u0447\\u043a\\u0438\\u00bb\"", "\"«ёлочки»\"")]
    [InlineData("\"\\uD83D\\uDE42\"", "\"🙂\"")]
    [InlineData("\"\\ud83d\\ude42\"", "\"🙂\"")]
    [InlineData("\"\\u003Cb\\u003E\\u0026\\u0027\\u002b\"", "\"<b>&'+\"")]
    // Не трогаем: управляющие, кавычка, косая, C1, U+2028/2029, одиночные суррогаты.
    [InlineData("\"\\u0000\\u001f\\u007F\\u0085\\u2028\\u2029\"", "\"\\u0000\\u001f\\u007F\\u0085\\u2028\\u2029\"")]
    // Кавычка, косая и управляющие с короткой формой пишутся ею.
    [InlineData("\"\\u0022\\u005c\\u000A\\u000d\\u0009\\u0008\\u000C\"", "\"\\\"\\\\\\n\\r\\t\\b\\f\"")]
    [InlineData("\"\\\\\\u0022\"", "\"\\\\\\\"\"")]
    [InlineData("\"\\uD83D x\\uDE42 \\uD83D\"", "\"\\uD83D x\\uDE42 \\uD83D\"")]
    [InlineData("\"\\uD83D\\u0041\"", "\"\\uD83DA\"")]
    // Обратная косая, за которой идёт «u0041», — не escape.
    [InlineData("\"\\\\u0041 \\n \\\" \\/\"", "\"\\\\u0041 \\n \\\" \\/\"")]
    [InlineData("\"\\\\\\u0041\"", "\"\\\\A\"")]
    // Мусор вместо hex и обрыв в конце не ломаются.
    [InlineData("\"\\uZZZZ \\u12", "\"\\uZZZZ \\u12")]
    [InlineData("\"abc\\", "\"abc\\")]
    [InlineData("\"\\uD83D", "\"\\uD83D")]
    public void Rewrites_only_what_is_safe(string input, string expected) => Assert.Equal(expected, Rewrite(input));

    [Fact]
    public void Chunk_boundaries_do_not_matter()
    {
        var json = "{\"a\":\"\\u041f\\u0440 \\uD83D\\uDE42 \\\\u0041 \\\\\\u0041 \\u2028 \\n \\uD83D\\\"\",\"b\":[\"\\u00ab\\u00bb\"]}";
        var expected = Rewrite(json);
        for (var chunk = 1; chunk <= json.Length; chunk++)
            Assert.Equal(expected, Rewrite(json, chunk));
        Assert.Equal("{\"a\":\"Пр 🙂 \\\\u0041 \\\\A \\u2028 \\n \\uD83D\\\"\",\"b\":[\"«»\"]}", expected);
    }

    [Fact]
    public void Result_is_the_same_json()
    {
        // Всё BMP кроме суррогатов и пара эмодзи: до и после — одно и то же значение.
        var text = new StringBuilder();
        for (var c = 0; c < 0x3000; c++)
            if (c is < 0xD800 or > 0xDFFF)
                text.Append((char)c);
        text.Append("🙂 \"quote\" \\ end");
        var sdk = JsonSerializer.Serialize(new { text = text.ToString() }); // стандартный кодировщик: всё в \uXXXX
        Assert.Contains("\\u", sdk);

        var wire = Rewrite(sdk);
        Assert.Equal(text.ToString(), JsonNode.Parse(wire)!["text"]!.GetValue<string>());
        Assert.True(wire.Length < sdk.Length);
        // Без выбранных для экранирования символов в обычном тексте \uXXXX не остаётся вовсе.
        Assert.DoesNotContain("\\u041", wire);
    }

    [Fact]
    public async Task Middleware_handles_json_sse_and_other_content()
    {
        var builder = WebApplication.CreateBuilder();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        const string Json = "{\"text\":\"\\u041f\\u0440\\u0438\\u0432\\u0435\\u0442 \\uD83D\\uDE42\"}";
        var group = app.MapGroup("/t");
        group.MapGet("/json", (HttpContext c) => { c.Response.ContentType = "application/json"; c.Response.ContentLength = Json.Length; return c.Response.WriteAsync(Json); });
        group.MapGet("/sse", async (HttpContext c) =>
        {
            c.Response.ContentType = "text/event-stream";
            // Событие приходит несколькими записями, с разрезом внутри \uXXXX и внутри пары суррогатов.
            foreach (var part in new[] { "event: message\ndata: {\"text\":\"\\u04", "1f\\u0440 \\uD83D", "\\uDE42\"}\n\n", "data: {\"n\":\"\\u0451\"}\n\n" })
            {
                await c.Response.WriteAsync(part);
                await c.Response.Body.FlushAsync();
            }
        });
        group.MapGet("/plain", (HttpContext c) => { c.Response.ContentType = "text/plain"; c.Response.ContentLength = 7; return c.Response.WriteAsync("\\u0041x"); });
        WireJson.Unescape(group);
        await app.StartAsync();
        var address = app.Urls.First();

        using var http = new HttpClient();
        using var json = await http.GetAsync($"{address}/t/json");
        var jsonText = await json.Content.ReadAsStringAsync();
        Assert.Equal("{\"text\":\"Привет 🙂\"}", jsonText);
        // Заявленная длина (58 байт) после замены неверна: она убрана, а фактическая длина — длина нового тела.
        Assert.Equal(Encoding.UTF8.GetByteCount(jsonText), json.Content.Headers.ContentLength ?? Encoding.UTF8.GetByteCount(jsonText));

        var sse = await http.GetStringAsync($"{address}/t/sse");
        Assert.Equal("event: message\ndata: {\"text\":\"Пр 🙂\"}\n\ndata: {\"n\":\"ё\"}\n\n", sse);

        using var plain = await http.GetAsync($"{address}/t/plain");
        Assert.Equal(7, plain.Content.Headers.ContentLength);
        Assert.Equal("\\u0041x", await plain.Content.ReadAsStringAsync());
    }
}
