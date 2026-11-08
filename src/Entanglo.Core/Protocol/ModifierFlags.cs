namespace Entanglo.Core.Protocol;

/// <summary>macOS CGEventFlags bitmask values used on the wire (PROTOCOL.md §5.5).</summary>
public static class ModifierFlags
{
    public const ulong CapsLock   = 1UL << 16; // 0x10000
    public const ulong Shift      = 1UL << 17; // 0x20000
    public const ulong Control    = 1UL << 18; // 0x40000
    public const ulong Option     = 1UL << 19; // 0x80000  (Alt on Windows)
    public const ulong Command    = 1UL << 20; // 0x100000 (Win key on Windows)
    public const ulong NumericPad = 1UL << 21; // 0x200000
    public const ulong Help       = 1UL << 22; // 0x400000
    public const ulong Function   = 1UL << 23; // 0x800000
}
