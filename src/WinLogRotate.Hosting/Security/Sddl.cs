namespace WinLogRotate.Hosting.Security;

/// <summary>
/// The security descriptor applied to the configuration directory.
/// </summary>
/// <remarks>
/// <para>
/// This is the most security-sensitive string in the product. The scheduled task runs as
/// SYSTEM and reads every file in <c>conf.d</c>, and jobs can carry hooks that execute
/// commands. Default <c>ProgramData</c> inheritance grants <c>BUILTIN\Users</c> the right to
/// create files and gives <c>CREATOR OWNER</c> full control of whatever they create - so
/// without hardening, any local user can drop a job file and have SYSTEM run their command at
/// three in the morning.
/// </para>
/// </remarks>
public static class Sddl
{
    /// <summary>
    /// SYSTEM and Administrators full control; Users read and execute only.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><c>O:BA</c> - owner is Administrators, set explicitly rather than
    /// inherited from whichever account ran the installer. An owner always has implicit
    /// WRITE_DAC, so ownership is equivalent to write access.</description></item>
    /// <item><description><c>D:P</c> - <b>protected</b>, severing inheritance from ProgramData.
    /// This flag is the load-bearing one: without it a correct DACL is a time bomb that
    /// re-inherits the permissive parent ACEs the moment anyone touches ProgramData.</description></item>
    /// <item><description><c>AI</c> - children inherit ours.</description></item>
    /// <item><description><c>0x1200a9</c> for Users is FILE_GENERIC_READ | FILE_EXECUTE. Read
    /// is deliberate: the unelevated read-only GUI is a shipped feature, and reading a job file
    /// grants nothing. The numeric mask is used rather than the FRFX shorthand because icacls
    /// and Get-Acl render the shorthand inconsistently, and this string appears in
    /// documentation and in three separate tests.</description></item>
    /// </list>
    /// </remarks>
    public const string ConfigDirectory =
        "O:BAG:SYD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)";

    /// <summary>
    /// Same, plus write access for a non-SYSTEM run account.
    /// </summary>
    /// <remarks>
    /// Only the state, journal and run directories ever get this - <c>conf.d</c> never does.
    /// The run account executes hooks, so granting it write access to the files that define
    /// those hooks would reintroduce exactly the escalation this design closes.
    /// </remarks>
    public static string WritableFor(string runAccountSid) =>
        $"O:BAG:SYD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;{runAccountSid})(A;OICI;0x1200a9;;;BU)";

    /// <summary>
    /// The secrets file: SYSTEM and Administrators, and deliberately no Users ACE at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one file under the configuration directory that breaks the "everyone may
    /// read" rule, and it is the only reason that rule survives everywhere else. The
    /// justification for Users read on <see cref="ConfigDirectory"/> is that reading a job file
    /// grants nothing. Reading a stored credential grants something, so it is not readable.
    /// </para>
    /// <para>
    /// No inheritance flags: this is a file, and a file has no children. No <c>AI</c>: nothing
    /// is inherited into it. <c>D:P</c> is what severs the OICI Users ACE the parent would
    /// otherwise supply - a file created in the configuration directory arrives readable by
    /// every local user unless this is applied explicitly, which is the whole hazard, and why
    /// it is applied to the temporary file before it is moved into place.
    /// </para>
    /// </remarks>
    public const string SecretsFile = "O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)";

    /// <summary>
    /// The secrets file for a per-user install, where the owner is not necessarily an
    /// administrator and would otherwise be locked out of their own store.
    /// </summary>
    public static string SecretsFileFor(string ownerSid) =>
        $"O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;{ownerSid})";

    /// <summary>
    /// The registry key holding the per-install secret entropy. Same principals as
    /// <see cref="SecretsFile"/>: entropy readable by everyone would not be entropy.
    /// </summary>
    public const string EntropyKey = "O:BAG:SYD:P(A;CI;KA;;;SY)(A;CI;KA;;;BA)";

    /// <summary>Well-known SIDs, used by name nowhere: on this machine the Administrators group
    /// is called <i>Administratoren</i>, and an icacls that fails on a localised name leaves the
    /// permissive inherited ACE in place - which is silent and is the whole hole.</summary>
    public static class WellKnown
    {
        public const string LocalSystem = "S-1-5-18";
        public const string Administrators = "S-1-5-32-544";
        public const string Users = "S-1-5-32-545";
        public const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
        public const string CreatorOwner = "S-1-3-0";
        public const string Everyone = "S-1-1-0";
        public const string AuthenticatedUsers = "S-1-5-11";
    }
}
