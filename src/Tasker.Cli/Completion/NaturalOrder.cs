namespace Tasker.Cli.Completion;

/// <summary>Порядок вариантов: без учёта регистра, числа по значению (<c>TSK-2</c> раньше <c>TSK-10</c>).</summary>
internal sealed class NaturalOrder : IComparer<string>
{
    public static NaturalOrder Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        x ??= "";
        y ??= "";
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                    i++;
                while (j < y.Length && char.IsAsciiDigit(y[j]))
                    j++;

                var numberX = x[startX..i].TrimStart('0');
                var numberY = y[startY..j].TrimStart('0');
                var byLength = numberX.Length.CompareTo(numberY.Length);
                if (byLength != 0)
                    return byLength;
                var byDigits = string.CompareOrdinal(numberX, numberY);
                if (byDigits != 0)
                    return byDigits;
                continue;
            }

            var difference = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (difference != 0)
                return difference;
            i++;
            j++;
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
