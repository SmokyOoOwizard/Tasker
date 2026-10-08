using Tasker.Cli;
using Xunit;

namespace Tasker.Tests;

/// <summary>Выравнивание колонок в консольных списках: <see cref="Table"/>.</summary>
public class TableTests
{
    private static string[] F(string indent, params string[][] rows) => Table.Format(rows, indent);

    [Fact]
    public void References_of_different_length_are_aligned()
    {
        Assert.Equal(
            ["T-7     Не начата  Баг  Один", "T-10    В работе   Баг  Два", "TSK-70  Готово     Баг  Три"],
            F("", ["T-7", "Не начата", "Баг", "Один"], ["T-10", "В работе", "Баг", "Два"], ["TSK-70", "Готово", "Баг", "Три"]));
    }

    [Fact]
    public void Width_is_the_display_width_not_the_utf16_length()
    {
        Assert.Equal(1, Table.Width("я"));
        Assert.Equal(2, Table.Width("🎉"));
        Assert.Equal(4, Table.Width("日本"));
        Assert.Equal(1, Table.Width("é"));
        Assert.Equal(2, Table.Width("❤️"));
        Assert.Equal(2, Table.Width("🇷🇺"));

        Assert.Equal(["A  🎉 раз  x", "B  日本    y", "C  é       z"], F("", ["A", "🎉 раз", "x"], ["B", "日本", "y"], ["C", "é", "z"]));
    }

    [Fact]
    public void The_last_column_is_not_padded_and_an_empty_cell_keeps_the_place()
    {
        Assert.Equal(
            ["TSK-1  Todo  Bug  Длинный заголовок", "TSK-2  Done       Короткий"],
            F("", ["TSK-1", "Todo", "Bug", "Длинный заголовок"], ["TSK-2", "Done", "", "Короткий"]));
    }

    [Fact]
    public void A_column_empty_in_every_row_is_left_out_and_the_indent_is_added()
    {
        Assert.Equal(["  TSK-1   Todo  One", "  TSK-22  Todo  Two"], F("  ", ["TSK-1", "Todo", "", "One"], ["TSK-22", "Todo", "", "Two"]));
    }

    [Fact]
    public void An_empty_list_is_empty()
    {
        Assert.Empty(F(""));
    }
}
