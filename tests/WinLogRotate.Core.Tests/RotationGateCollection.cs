using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The suites that drive the <c>run</c> verb in this process, run one at a time.
/// </summary>
/// <remarks>
/// <para>
/// <c>run</c> takes a machine-wide kernel mutex, and xunit runs one collection per class in
/// parallel by default. Two of these classes overlapping is what exposed the rotation gate's
/// defect on the Windows leg - and once the gate is correct, overlapping is worse rather than
/// better: the second caller is politely refused with exit 3 and rotates nothing, so a test
/// expecting a completed run fails only sometimes. Serialising them turns an intermittent
/// failure back into a deterministic pass.
/// </para>
/// <para>
/// Not <c>--skip-state-lock</c>, which would have been the obvious alternative. It emits a
/// warning diagnostic, which breaks the one test whose entire fixture is a run with nothing to
/// say; and it would make <c>JsonStreamTests</c>' assertion that "the GUI's own command line
/// must parse" false, since the GUI passes no such flag. The point of those tests is that they
/// run the real thing with the real arguments.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class RotationGateCollection
{
    public const string Name = "takes the rotation gate";
}
