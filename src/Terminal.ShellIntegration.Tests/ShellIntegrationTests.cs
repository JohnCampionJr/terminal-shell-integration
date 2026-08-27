using NUnit.Framework;
using Terminal.ShellIntegration;

namespace Terminal.ShellIntegration.Tests;

/// <summary>
/// What gets spawned instead, per shell.
/// </summary>
/// <remarks>
/// The transform is pure, which is the reason it lives in a library of its own: every shell quirk
/// below is covered by asserting on arguments and environment, with nothing spawned and no terminal
/// involved. Testing this by launching shells would need three shells installed and would still not
/// tell you WHY a launch went wrong.
/// </remarks>
[TestFixture]
public class ShellIntegrationTests
{
    private string _resources = string.Empty;

    [SetUp]
    public void CreateResources()
    {
        // The real layout, because Prepare refuses to instrument a shell whose scripts are absent.
        _resources = Path.Combine(Path.GetTempPath(), "shell-integration-tests", Guid.NewGuid().ToString("n"));
        foreach (var shell in new[] { "bash", "zsh", "fish" })
            Directory.CreateDirectory(Path.Combine(_resources, shell));
    }

    [TearDown]
    public void RemoveResources()
    {
        if (Directory.Exists(_resources))
            Directory.Delete(_resources, recursive: true);
    }

    private ShellIntegrationResult Prepare(
        string shell, IReadOnlyList<string>? args = null, IReadOnlyDictionary<string, string>? env = null)
        => ShellIntegration.Prepare(shell, args, env ?? new Dictionary<string, string>(), _resources);

    // ---- which shell is it ----------------------------------------------------------------------

    [TestCase("/bin/bash", ShellKind.Bash)]
    [TestCase("/usr/local/bin/zsh", ShellKind.Zsh)]
    [TestCase("fish", ShellKind.Fish)]
    [TestCase("/bin/sh", ShellKind.Unknown)]
    [TestCase("", ShellKind.Unknown)]
    public void Identifies_the_shell(string path, ShellKind expected)
        => Assert.That(ShellIntegration.Identify(path), Is.EqualTo(expected));

    /// <summary>
    /// A login shell is conventionally spawned with its name prefixed by a dash, and it is still the
    /// same shell — missing this instruments nobody who logs in.
    /// </summary>
    [TestCase("-bash", ShellKind.Bash)]
    [TestCase("-zsh", ShellKind.Zsh)]
    public void A_login_shell_is_still_the_same_shell(string path, ShellKind expected)
        => Assert.That(ShellIntegration.Identify(path), Is.EqualTo(expected));

    [TestCase("bash.exe", ShellKind.Bash)]
    [TestCase("C:\\Program Files\\Git\\bin\\bash.exe", ShellKind.Bash)]
    public void Windows_names_are_recognised(string path, ShellKind expected)
        => Assert.That(ShellIntegration.Identify(path), Is.EqualTo(expected));

    // ---- when to leave a shell alone ------------------------------------------------------------

    [Test]
    public void An_unsupported_shell_is_untouched()
    {
        var result = Prepare("/bin/sh", new[] { "-i" });

        Assert.That(result.Injected, Is.False);
        Assert.That(result.Skipped, Is.EqualTo(SkipReason.UnsupportedShell));
        Assert.That(result.Args, Is.EqualTo(new[] { "-i" }));
    }

    /// <summary>
    /// A shell spawned from an instrumented one would be instrumented twice, and would then emit
    /// every mark twice.
    /// </summary>
    [Test]
    public void A_shell_inside_an_instrumented_one_is_left_alone()
    {
        var env = new Dictionary<string, string> { [ShellIntegration.MarkerVariable] = "1" };

        var result = Prepare("/bin/bash", null, env);

        Assert.That(result.Injected, Is.False);
        Assert.That(result.Skipped, Is.EqualTo(SkipReason.AlreadyInjected));
    }

    /// <summary>
    /// <c>-c</c> runs a command and exits: no prompt to mark, and rewriting the arguments of a
    /// one-shot command is a good way to break it.
    /// </summary>
    [TestCase("-c")]
    [TestCase("--command")]
    [TestCase("-lc")]
    [TestCase("-ic")]
    public void A_command_shell_is_left_alone(string arg)
    {
        var result = Prepare("/bin/bash", new[] { arg, "echo hi" });

        Assert.That(result.Injected, Is.False);
        Assert.That(result.Skipped, Is.EqualTo(SkipReason.NotInteractive));
        Assert.That(result.Args, Is.EqualTo(new[] { arg, "echo hi" }), "the command must reach the shell unchanged");
    }

    /// <summary>
    /// Better to run an uninstrumented shell than to point one at scripts that are not there.
    /// </summary>
    [Test]
    public void Missing_scripts_mean_no_injection()
    {
        var result = ShellIntegration.Prepare("/bin/bash", null, new Dictionary<string, string>(),
                                              Path.Combine(_resources, "nowhere"));

        Assert.That(result.Injected, Is.False);
        Assert.That(result.Skipped, Is.EqualTo(SkipReason.ResourcesMissing));
    }

    // ---- bash -----------------------------------------------------------------------------------

    /// <summary>
    /// POSIX mode is the whole trick: <c>--init-file</c> is silently ignored for a login shell,
    /// while <c>$ENV</c> is read whatever kind of shell it is.
    /// </summary>
    [Test]
    public void Bash_is_started_in_posix_mode_pointing_at_the_script()
    {
        var result = Prepare("/bin/bash", new[] { "-i" });

        Assert.That(result.Injected, Is.True);
        Assert.That(result.Args[0], Is.EqualTo("--posix"), "--posix must come before the rest");
        Assert.That(result.Args, Does.Contain("-i"), "the caller's own arguments must survive");

        Assert.That(result.Environment["ENV"],
            Is.EqualTo(Path.Combine(_resources, "bash", "integration.bash")));
    }

    /// <summary>
    /// POSIX mode displaces the user's own ENV, so the script needs it back.
    /// </summary>
    [Test]
    public void Bash_keeps_the_users_ENV_for_the_script_to_restore()
    {
        var env = new Dictionary<string, string> { ["ENV"] = "/home/someone/.env" };

        var result = Prepare("/bin/bash", null, env);

        Assert.That(result.Environment[ShellIntegration.BashEnvVariable], Is.EqualTo("/home/someone/.env"));
        Assert.That(result.Environment["ENV"], Is.Not.EqualTo("/home/someone/.env"));
    }

    /// <summary>
    /// The script has to replay bash's startup, and it can only do that faithfully if it is told
    /// which flags bash was given.
    /// </summary>
    [TestCase("-l", "login")]
    [TestCase("--login", "login")]
    [TestCase("--noprofile", "noprofile")]
    [TestCase("--norc", "norc")]
    public void Bash_passes_its_startup_flags_to_the_script(string arg, string expected)
    {
        var result = Prepare("/bin/bash", new[] { arg });

        Assert.That(result.Environment[ShellIntegration.BashInjectVariable], Does.Contain(expected));
    }

    // ---- zsh ------------------------------------------------------------------------------------

    [Test]
    public void Zsh_is_pointed_at_the_injected_ZDOTDIR()
    {
        var result = Prepare("/bin/zsh");

        Assert.That(result.Injected, Is.True);
        Assert.That(result.Environment["ZDOTDIR"], Is.EqualTo(Path.Combine(_resources, "zsh")));
    }

    /// <summary>
    /// The injected rc restores this before sourcing anything of the user's, so a nested zsh reads
    /// their configuration rather than the integration again.
    /// </summary>
    [Test]
    public void Zsh_keeps_the_users_own_ZDOTDIR()
    {
        var env = new Dictionary<string, string> { ["ZDOTDIR"] = "/home/someone/.config/zsh" };

        var result = Prepare("/bin/zsh", null, env);

        Assert.That(result.Environment[ShellIntegration.ZshDotDirVariable],
            Is.EqualTo("/home/someone/.config/zsh"));
    }

    /// <summary>
    /// With no ZDOTDIR set, zsh reads from HOME — so that is what has to be restored, not nothing.
    /// </summary>
    [Test]
    public void Zsh_falls_back_to_HOME_when_no_ZDOTDIR_is_set()
    {
        var env = new Dictionary<string, string> { ["HOME"] = "/home/someone" };

        var result = Prepare("/bin/zsh", null, env);

        Assert.That(result.Environment[ShellIntegration.ZshDotDirVariable], Is.EqualTo("/home/someone"));
    }

    // ---- fish -----------------------------------------------------------------------------------

    /// <summary>
    /// The entry is the PARENT of the fish directory, because fish searches each data directory for
    /// <c>fish/vendor_conf.d</c> — pointing it at the fish folder makes it look for
    /// <c>fish/fish/vendor_conf.d</c>, find nothing, and load nothing without complaining.
    /// </summary>
    [Test]
    public void Fish_gets_the_tree_on_its_data_path()
    {
        var result = Prepare("/usr/local/bin/fish");

        Assert.That(result.Injected, Is.True);
        Assert.That(result.Environment["XDG_DATA_DIRS"], Is.EqualTo(_resources));
        Assert.That(Directory.Exists(Path.Combine(result.Environment["XDG_DATA_DIRS"], "fish")), Is.True,
            "the entry must be the directory that CONTAINS fish/, which is what fish searches");
    }

    /// <summary>
    /// Prepended rather than replacing: XDG_DATA_DIRS is a search path with other things on it, and
    /// overwriting it would take away whatever else the user had.
    /// </summary>
    [Test]
    public void Fish_keeps_the_existing_data_path()
    {
        var env = new Dictionary<string, string> { ["XDG_DATA_DIRS"] = "/usr/share" };

        var result = Prepare("/usr/local/bin/fish", null, env);

        var dirs = result.Environment["XDG_DATA_DIRS"].Split(Path.PathSeparator);
        Assert.That(dirs[0], Is.EqualTo(_resources));
        Assert.That(dirs, Does.Contain("/usr/share"));
    }

    // ---- what every instrumented shell gets -----------------------------------------------------

    [TestCase("/bin/bash")]
    [TestCase("/bin/zsh")]
    [TestCase("/usr/local/bin/fish")]
    public void An_instrumented_shell_is_marked_and_told_where_the_scripts_are(string shell)
    {
        var result = Prepare(shell);

        Assert.That(result.Environment[ShellIntegration.MarkerVariable], Is.EqualTo("1"));
        Assert.That(result.Environment[ShellIntegration.ResourcesVariable], Is.EqualTo(_resources));
    }

    /// <summary>
    /// The caller's environment is not modified — a host reusing the dictionary it passed in would
    /// otherwise accumulate the marker and instrument nothing on the second launch.
    /// </summary>
    [Test]
    public void The_callers_environment_is_left_alone()
    {
        var env = new Dictionary<string, string> { ["HOME"] = "/home/someone" };

        ShellIntegration.Prepare("/bin/zsh", null, env, _resources);

        Assert.That(env.ContainsKey(ShellIntegration.MarkerVariable), Is.False);
        Assert.That(env.ContainsKey("ZDOTDIR"), Is.False);
    }
}
