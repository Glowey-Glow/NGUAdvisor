using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("NGU Advisor")]
[assembly: AssemblyDescription("")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("NGU Advisor")]
[assembly: AssemblyCopyright("Copyright ©  2026")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("2f930638-ea52-446d-ad62-4ebec6a03556")]

// Version information for an assembly consists of the following four values:
//
//      Major Version
//      Minor Version
//      Build Number
//      Revision
//
// You can specify all the values or you can default the Build and Revision Numbers
// by using the '*' as shown below:
// [assembly: AssemblyVersion("1.0.*")]
//
// ⚠ KEEP MAJOR.MINOR IN STEP WITH Main.cs's `Version` const. That const is the product version and the
// only thing package-release.sh scrapes (:45, which then names the zip, the stage dir and the git tag),
// but THESE TWO ATTRIBUTES are what the shipped DLL actually reports: NGUAdvisor.csproj sets
// GenerateAssemblyInfo=false, so this file is the sole source of the version resource and File
// Properties on NGUAdvisor.dll shows AssemblyFileVersion, nothing else. The two drifted four minor
// versions apart — the zip, the tag, the inject.log header, README and CHANGELOG all said 2.2.0 while
// the DLL said 1.1.0.0.
//
// The '*' stays. It is live, not boilerplate: it expands to <major>.<minor>.<days since 2000-01-01>.
// <seconds since midnight / 2> and it is the whole reason NGUAdvisor.csproj sets Deterministic=false.
// Keeping it is
// free because nothing binds this assembly by version — it is unsigned (PublicKeyToken=null) and is
// reached only by smi injection and Assembly.Load(byte[]) + reflection, never by assembly identity.
// So AssemblyVersion agrees on major.minor; AssemblyFileVersion, the informational one a player reads,
// agrees exactly.
[assembly: AssemblyVersion("2.3.*")]
[assembly: AssemblyFileVersion("2.3.0.0")]
