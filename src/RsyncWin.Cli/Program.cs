using RsyncWin.Protocol.Session;

// Flag parsing arrives at P3 (--list-only is the first command that does real work).
// Until then the CLI exists so the exit-code contract is exercised from day one:
// rsyncwin returns rsync's numeric statuses verbatim, so wrapper scripts keep working.
Console.Error.WriteLine("rsyncwin: not implemented yet (protocol core is under construction)");
return (int)RsyncExitCode.UnsupportedAction;
