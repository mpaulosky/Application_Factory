#:include Gh.cs
#:include CheckCentralPins.cs
#:property PublishAot=false
// Fail on a central NuGet pin that no project references, under transitive pinning. CheckCentralPins.cs holds the logic; Gh.cs the
// argument parser and Script.Run.
//
// Run by scripts/gate.sh before the build, and by the Build Solution job in .github/workflows/ci.yml right after Setup .NET, each after a
// step that builds it (dotnet build .github/scripts/check_central_pins.cs):
//
//     dotnet run --no-build --file .github/scripts/check_central_pins.cs -- --root .
//
// Under central package management with CentralPackageTransitivePinningEnabled, a PackageVersion in Directory.Packages.props that no
// project references still pins the package wherever it comes in through another one. Dependabot doesn't raise such a pin when it bumps
// the package that needs it, so restore fails with NU1109 until someone raises it by hand (mpaulosky/IssueManager#340 and
// mpaulosky/IssueManager#351). Deleting the pin lets NuGet resolve the version the parent package asks for.
//
// A pin kept on purpose, such as one that forces a package up past a security advisory, or one for a package an SDK references itself
// (MSTest.Sdk's, say), is allowed when the XML comment right before it starts with "Transitive pin:" (any case) and gives the reason:
//
//     <!-- Transitive pin: GHSA-xxxx-xxxx-xxxx, fixed in 4.3.1; System.Net.Http comes in through Foo. -->
//     <PackageVersion Include="System.Net.Http" Version="4.3.4" />
//
// What counts as a reference: an id in a PackageReference (Include or Update), GlobalPackageReference or PackageDownload, or an MSBuild
// SDK (a Project Sdk attribute, an Sdk element, an Import's Sdk, or a global.json's msbuild-sdks), in any MSBuild file in the repo: a
// file whose extension ends in "proj" (.csproj, .sqlproj, .wixproj, ...), or a .props or .targets file. NuGet ids are compared ignoring
// case. An id holding $(Name) is resolved from the values the repo's MSBuild files give that property, wherever they set it. One that
// still holds $(, @( or %(, that is over MaxId characters, or that would need more than MaxExpansions combinations of values or grow
// past MaxValue characters with them substituted (any property value over MaxValue does), can't be read without evaluating MSBuild. A
// pin like that is left alone, and a reference like that could be any package, so the pins found are then reported as warnings and the
// check passes. Transitive pinning is on when any of those files sets the property to true, or to a value built from $(Name) that
// resolves, the same way and within the same limits, to true for one combination of values. A value without $(, @( or %( is compared
// as written. Unless transitive pinning is on anyway (another setting, in any file, is true or resolves to true), a value that holds
// them and can't be resolved counts as on, named in a notice, so the check fails closed: under it an unreferenced pin either holds a
// package back or does nothing.
//
// Every file is read as bytes, so the XML reader honours its byte-order mark and XML declaration (MSBuild reads UTF-16 project files
// too). Only regular files are read, not symlinks: an MSBuild file of at most MaxBytes, a global.json of at most MaxGlobalJsonBytes.
// A DOCTYPE is refused: MSBuild files never need one, and entity expansion is how a small file grows huge. So are an element nested
// over MaxDepth deep and an encoding .NET doesn't decode without a code page provider (it decodes UTF-8, UTF-16, UTF-32, ASCII and
// ISO-8859-1). A file that can't be read or parsed can't be checked:
//
// - An MSBuild file that may hold pins or turn transitive pinning on (a Directory.Packages.props, one that names PackageVersion or
//   CentralPackageTransitivePinningEnabled, or one whose bytes weren't read at all) fails the check, naming the file and why: skipping
//   it could hide a pin or the setting. So does a folder that can't be listed, or a symlinked folder, which isn't walked. Under
//   transitive pinning, so does any MSBuild file that couldn't be parsed when a central pin without a reason holds $( (is built from
//   a property), since the file may give the property a value, and so a pin, the check never sees.
// - Any other MSBuild file, and a global.json that can't be read (a symlink, a FIFO, one over MaxGlobalJsonBytes, or not JSON the
//   .NET host reads, nested at most 1,000 deep), is skipped with a notice, naming it. It can hide references, which makes the check
//   stricter. It can also hide the value of a property a reference is built from, which makes that reference unreadable: the pins
//   found are then warnings and the check passes, naming the reference, as for any reference the check can't read.
//
// Only the file itself is opened without following a symlink or waiting on a FIFO (open's O_NOFOLLOW and O_NONBLOCK, through libc,
// since the BCL has neither), then checked again as the open file: a folder swapped for one between the walk and the read is followed,
// which needs a process writing to the tree while the check runs, not a change in a pull request.
//
// bin/, obj/, node_modules/ and dot-folders (.git, CI's .nuget package cache) aren't walked. The whole run reads at most MaxFiles files,
// MaxTotalBytes, MaxNodes elements, attributes and comments, MaxIds ids and MaxTotalExpansions combinations of property values; past
// any of them it fails, since it can't vouch for what it didn't read. Only the first MaxGlobalJsonFiles global.json files are read,
// the shallowest first; the rest are skipped with a notice. Every entry of an id list counts as an id, blank or not (an absent or
// empty attribute counts none): a reference $(P) is one, and the first time a name is resolved, every entry of it with its properties
// substituted counts too, once for each combination of values ($(P) with P set to A;B;C is four in all). So does every SDK a
// global.json names. Each kind of line is printed at most MaxShown times, and each name or path in it is cut after MaxPrinted
// characters, with a count of the rest. The caps are CheckCentralPins.cs's Caps.
//
// A run with a file or folder that fails the check always prints "Not a pass" as its last line on stdout, after any pins it found; one
// that only skipped files with a notice ends as it would without them.
//
// Under GitHub Actions each problem is an annotation, its text escaped as the runner reads workflow commands; elsewhere a plain line.
// Every text a repo controls is printed with its non-printable characters as '?'.
//
// Exit 0 on a pass; 1 on an unreferenced pin, a file or folder that can't be checked, or a repo past the run's budget; 2 on a usage
// error (argparse's, as check_central_pins.py gave: a --root that isn't a folder, an option may be given by a unique prefix of its
// name). The standard library only, and libc's open. check_central_pins.py, beside it, is the same script in Python, kept for one
// Rollout so a revert is one workflow line.
using Baseline;

return Script.Run((stdout, stderr) => new CentralPins().Run(args, stdout, stderr));
