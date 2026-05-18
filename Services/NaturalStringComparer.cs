namespace ChaoticWebtoonReader.Services;

internal sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        x ??= string.Empty;
        y ??= string.Empty;

        var leftIndex = 0;
        var rightIndex = 0;

        while (leftIndex < x.Length && rightIndex < y.Length)
        {
            var left = x[leftIndex];
            var right = y[rightIndex];

            if (char.IsDigit(left) && char.IsDigit(right))
            {
                var result = CompareNumberRun(x, ref leftIndex, y, ref rightIndex);
                if (result != 0)
                {
                    return result;
                }

                continue;
            }

            var charCompare = char.ToUpperInvariant(left).CompareTo(char.ToUpperInvariant(right));
            if (charCompare != 0)
            {
                return charCompare;
            }

            leftIndex++;
            rightIndex++;
        }

        return (x.Length - leftIndex).CompareTo(y.Length - rightIndex);
    }

    private static int CompareNumberRun(string left, ref int leftIndex, string right, ref int rightIndex)
    {
        var leftStart = leftIndex;
        var rightStart = rightIndex;

        while (leftIndex < left.Length && char.IsDigit(left[leftIndex]))
        {
            leftIndex++;
        }

        while (rightIndex < right.Length && char.IsDigit(right[rightIndex]))
        {
            rightIndex++;
        }

        var leftRun = left[leftStart..leftIndex];
        var rightRun = right[rightStart..rightIndex];
        var leftNumber = leftRun.TrimStart('0');
        var rightNumber = rightRun.TrimStart('0');

        if (leftNumber.Length == 0)
        {
            leftNumber = "0";
        }

        if (rightNumber.Length == 0)
        {
            rightNumber = "0";
        }

        var lengthCompare = leftNumber.Length.CompareTo(rightNumber.Length);
        if (lengthCompare != 0)
        {
            return lengthCompare;
        }

        var valueCompare = string.CompareOrdinal(leftNumber, rightNumber);
        if (valueCompare != 0)
        {
            return valueCompare;
        }

        return leftRun.Length.CompareTo(rightRun.Length);
    }
}
