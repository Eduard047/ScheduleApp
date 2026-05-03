using System.Collections.Generic;
using System.Globalization;

namespace BlazorWasmDotNet8AspNetCoreHosted.Client.Utilities;

public sealed class NaturalStringComparer : IComparer<string?>
{
    public static NaturalStringComparer CurrentCultureIgnoreCase { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        var left = x ?? string.Empty;
        var right = y ?? string.Empty;
        var leftIndex = 0;
        var rightIndex = 0;

        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            var result = IsDigit(left[leftIndex]) && IsDigit(right[rightIndex])
                ? CompareNumberSegment(left, ref leftIndex, right, ref rightIndex)
                : CompareTextSegment(left, ref leftIndex, right, ref rightIndex);

            if (result != 0)
            {
                return result;
            }
        }

        return (left.Length - leftIndex).CompareTo(right.Length - rightIndex);
    }

    private static int CompareTextSegment(string left, ref int leftIndex, string right, ref int rightIndex)
    {
        var leftStart = leftIndex;
        var rightStart = rightIndex;

        while (leftIndex < left.Length && !IsDigit(left[leftIndex]))
        {
            leftIndex++;
        }

        while (rightIndex < right.Length && !IsDigit(right[rightIndex]))
        {
            rightIndex++;
        }

        return CultureInfo.CurrentCulture.CompareInfo.Compare(
            left,
            leftStart,
            leftIndex - leftStart,
            right,
            rightStart,
            rightIndex - rightStart,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
    }

    private static int CompareNumberSegment(string left, ref int leftIndex, string right, ref int rightIndex)
    {
        var leftStart = leftIndex;
        var rightStart = rightIndex;

        while (leftIndex < left.Length && IsDigit(left[leftIndex]))
        {
            leftIndex++;
        }

        while (rightIndex < right.Length && IsDigit(right[rightIndex]))
        {
            rightIndex++;
        }

        var leftTrimmedStart = SkipLeadingZeroes(left, leftStart, leftIndex);
        var rightTrimmedStart = SkipLeadingZeroes(right, rightStart, rightIndex);
        var leftDigitCount = leftIndex - leftTrimmedStart;
        var rightDigitCount = rightIndex - rightTrimmedStart;

        if (leftDigitCount != rightDigitCount)
        {
            return leftDigitCount.CompareTo(rightDigitCount);
        }

        for (var offset = 0; offset < leftDigitCount; offset++)
        {
            var result = left[leftTrimmedStart + offset].CompareTo(right[rightTrimmedStart + offset]);
            if (result != 0)
            {
                return result;
            }
        }

        return (leftIndex - leftStart).CompareTo(rightIndex - rightStart);
    }

    private static int SkipLeadingZeroes(string value, int start, int end)
    {
        while (start < end - 1 && value[start] == '0')
        {
            start++;
        }

        return start;
    }

    private static bool IsDigit(char value)
        => value is >= '0' and <= '9';
}
