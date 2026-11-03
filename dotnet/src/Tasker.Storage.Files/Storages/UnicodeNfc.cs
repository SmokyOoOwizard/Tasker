using System.Runtime.InteropServices;
using System.Text;

namespace Tasker.Storage.Files.Storages;

internal static partial class UnicodeNfc
{
    private static readonly bool Invariant = AppContext.TryGetSwitch("System.Globalization.Invariant", out var invariant) && invariant;

    public static string Normalize(string text)
    {
        if (text.AsSpan().IndexOfAnyInRange('̀', '￿') < 0)
            return text;
        if (!Invariant)
            return text.Normalize(NormalizationForm.FormC);
        if (OperatingSystem.IsMacOS())
            return MacNormalize(text);
        if (OperatingSystem.IsWindows())
            return WindowsNormalize(text);
        return text.Normalize(NormalizationForm.FormC);
    }

    private const long NormalizationFormC = 2;

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial nint CFStringCreateWithCharacters(nint allocator, char* chars, nint length);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial nint CFStringCreateMutableCopy(nint allocator, nint maxLength, nint source);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial void CFStringNormalize(nint text, long form);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial nint CFStringGetLength(nint text);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static unsafe partial void CFStringGetCharacters(nint text, CFRange range, char* buffer);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial void CFRelease(nint value);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CFRange(nint Location, nint Length);

    private static unsafe string MacNormalize(string text)
    {
        nint source;
        fixed (char* chars = text)
            source = CFStringCreateWithCharacters(0, chars, text.Length);
        if (source == 0)
            return text;
        var mutable = CFStringCreateMutableCopy(0, 0, source);
        CFRelease(source);
        if (mutable == 0)
            return text;
        try
        {
            CFStringNormalize(mutable, NormalizationFormC);
            var length = CFStringGetLength(mutable);
            var result = new string('\0', (int)length);
            fixed (char* buffer = result)
                CFStringGetCharacters(mutable, new CFRange(0, length), buffer);
            return result;
        }
        finally
        {
            CFRelease(mutable);
        }
    }

    [LibraryImport("normaliz.dll", SetLastError = true)]
    private static unsafe partial int NormalizeString(int form, char* source, int sourceLength, char* destination, int destinationLength);

    private static unsafe string WindowsNormalize(string text)
    {
        fixed (char* source = text)
        {
            var size = NormalizeString(1, source, text.Length, null, 0);
            if (size <= 0)
                return text;
            var buffer = new char[size];
            fixed (char* destination = buffer)
            {
                var written = NormalizeString(1, source, text.Length, destination, size);
                return written <= 0 ? text : new string(buffer, 0, written);
            }
        }
    }
}
