using System.Collections.Frozen;

namespace MacMonitor.Ssh;

/// <summary>
/// The allow-list of commands that the SSH executor will run. Templates may contain
/// <c>{name}</c> placeholders which are replaced by sanitized arg values at execution time.
/// Anything not in this dictionary is rejected.
/// </summary>
/// <remarks>
/// Why a registry rather than free-form command strings: the AI agent in Phase 3 will be
/// able to ask for tool calls. Tool calls map to entries here. The model never sees and
/// never gets to inject the actual shell template — the only thing it controls is
/// (a) which command id to run, and (b) the placeholder values, which are sanitized.
/// </remarks>
public static class CommandRegistry
{
    public static readonly FrozenDictionary<string, CommandSpec> Commands = new Dictionary<string, CommandSpec>(StringComparer.Ordinal)
    {
        // ──────────────── Phase 1: list tools ────────────────

        ["list-processes"] = new(
            Id: "list-processes",
            Template: "/bin/ps -axww -o pid=,ppid=,user=,%cpu=,%mem=,command=",
            ParameterNames: Array.Empty<string>()),

        ["list-launch-items"] = new(
            Id: "list-launch-items",
            // %N=name %m=mtime epoch %z=size — pipe-delimited and easy to parse.
            // Use 'find ... -print0 | xargs -0 stat' to be robust to spaces.
            Template:
                "/usr/bin/find /Library/LaunchAgents \"$HOME/Library/LaunchAgents\" /Library/LaunchDaemons /System/Library/LaunchDaemons " +
                "-maxdepth 2 -name '*.plist' -type f -print0 2>/dev/null | " +
                "/usr/bin/xargs -0 /usr/bin/stat -f '%N|%m|%z' 2>/dev/null",
            ParameterNames: Array.Empty<string>()),

        ["network-connections"] = new(
            Id: "network-connections",
            // -F gives field-tagged output; we ask for p (pid), c (command), P (proto), n (name), T (TCP state).
            Template: "/usr/sbin/lsof -nP -iTCP -iUDP -F pcPnT 2>/dev/null",
            ParameterNames: Array.Empty<string>()),

        ["recent-downloads"] = new(
            Id: "recent-downloads",
            // Files modified in the last 30 days. Quarantine xattr is fetched separately to keep
            // this command's output schema stable; missing xattr just yields blank in column 5.
            Template:
                "/usr/bin/find \"$HOME/Downloads\" -maxdepth 1 -type f -mtime -30 -print0 2>/dev/null | " +
                "/usr/bin/xargs -0 -I{} /bin/sh -c " +
                "'q=$(/usr/bin/xattr -p com.apple.quarantine \"{}\" 2>/dev/null | tr \"\\n\" \" \" | sed \"s/[|]/_/g\"); " +
                "/usr/bin/stat -f \"%N|%m|%z|%Su|$q\" \"{}\"' 2>/dev/null",
            ParameterNames: Array.Empty<string>()),

        // ──────────────── Phase 3: agent detail tools ────────────────
        //
        // These are invoked by the Claude triage loop, not the orchestrator. Parameters
        // come from the model and are single-quote-escaped before substitution into the
        // template (see SshExecutor.Render).

        ["process-detail"] = new(
            Id: "process-detail",
            // Open files for the pid, then the parent chain (pid → command → user up the
            // tree to launchd), then codesign info for the executable.
            Template:
                "echo '---LSOF---' && /usr/sbin/lsof -p {pid} 2>/dev/null; " +
                "echo '---ANCESTRY---' && p={pid}; while [ \"$p\" != \"0\" ] && [ \"$p\" != \"1\" ]; do " +
                "  /bin/ps -p $p -o pid=,ppid=,user=,command= 2>/dev/null || break; " +
                "  p=$(/bin/ps -p $p -o ppid= 2>/dev/null | /usr/bin/tr -d ' '); " +
                "done; " +
                "echo '---CODESIGN---' && exe=$(/bin/ps -p {pid} -o command= 2>/dev/null | /usr/bin/awk '{print $1}'); " +
                "[ -n \"$exe\" ] && /usr/bin/codesign -dv --verbose=4 \"$exe\" 2>&1 || echo 'no-exe'",
            ParameterNames: new[] { "pid" }),

        ["read-launch-plist"] = new(
            Id: "read-launch-plist",
            Template: "/usr/bin/plutil -convert xml1 -o - {path} 2>/dev/null",
            ParameterNames: new[] { "path" }),

        ["verify-signature"] = new(
            Id: "verify-signature",
            Template:
                "echo '---CODESIGN---' && /usr/bin/codesign --verify --deep --strict --verbose=4 {path} 2>&1; " +
                "echo '---SPCTL---' && /usr/sbin/spctl --assess --type execute {path} 2>&1",
            ParameterNames: new[] { "path" }),

        ["hash-file"] = new(
            Id: "hash-file",
            Template: "/usr/bin/shasum -a 256 {path}",
            ParameterNames: new[] { "path" }),

        ["quarantine-events"] = new(
            Id: "quarantine-events",
            // Last 50 quarantine events: timestamp, originating agent (browser/etc), data URL, origin URL.
            Template:
                "/usr/bin/sqlite3 -batch -separator '|' " +
                "\"$HOME/Library/Preferences/com.apple.LaunchServices.QuarantineEventsV2\" " +
                "\"SELECT LSQuarantineTimeStamp, LSQuarantineAgentName, LSQuarantineDataURLString, LSQuarantineOriginURLString " +
                "FROM LSQuarantineEvent ORDER BY LSQuarantineTimeStamp DESC LIMIT 50\" 2>/dev/null",
            ParameterNames: Array.Empty<string>()),

        // ──────────────── Phase 0 sanity check ────────────────

        ["whoami"] = new(
            Id: "whoami",
            Template: "/usr/bin/whoami",
            ParameterNames: Array.Empty<string>()),
    }.ToFrozenDictionary();

    public sealed record CommandSpec(string Id, string Template, IReadOnlyList<string> ParameterNames);
}
