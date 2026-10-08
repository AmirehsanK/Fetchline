using Fetchline.Core.Isa;

namespace Fetchline.Core.Trace;

/// <summary>
/// A hash of everything a run did, built field by field with 64-bit FNV-1a. It is spelled out
/// here, and does not lean on <see cref="object.GetHashCode"/>, so that the same program gives
/// the same number on every machine, runtime and browser. That is the determinism the playground
/// depends on: stepping back is replaying, and a share link reproduces a cycle.
/// </summary>
public struct TraceHash
{
    private const ulong OffsetBasis = 14695981039346656037;
    private const ulong Prime = 1099511628211;

    private ulong _state;
    private bool _started;

    /// <summary>The hash of everything added so far.</summary>
    public readonly ulong Value => _started ? _state : OffsetBasis;

    public void Add(byte value)
    {
        if (!_started)
        {
            _state = OffsetBasis;
            _started = true;
        }

        _state = (_state ^ value) * Prime;
    }

    public void Add(bool value) => Add((byte)(value ? 1 : 0));

    public void Add(uint value)
    {
        Add((byte)value);
        Add((byte)(value >> 8));
        Add((byte)(value >> 16));
        Add((byte)(value >> 24));
    }

    public void Add(int value) => Add((uint)value);

    public void Add(ulong value)
    {
        Add((uint)value);
        Add((uint)(value >> 32));
    }

    public void Add(string? text)
    {
        if (text is null)
        {
            Add(uint.MaxValue);
            return;
        }

        Add((uint)text.Length);
        foreach (var c in text)
        {
            Add((byte)c);
            Add((byte)(c >> 8));
        }
    }

    public void Add(in Instruction instruction)
    {
        Add((byte)instruction.Op);
        Add(instruction.Raw);
    }

    public void Add(in Commit commit)
    {
        Add(commit.Pc);
        Add(commit.Instruction);
        Add(commit.Register);
        Add(commit.Value);
        Add(commit.StoreBytes);
        Add(commit.StoreAddress);
        Add(commit.StoreValue);
        Add(commit.Trapped);
        Add(commit.Cause);
        Add(commit.TrapValue);
        Add(commit.NextPc);
        Add((byte)commit.Stop);
        Add(commit.ExitCode);
        Add(commit.Message);
    }
}
