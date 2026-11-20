# Upper chord transaction offline regression

Uses the actual independently compiled application DLL. Tests create only text fixtures under the test output directory. They do not instantiate SolidWorksService, connect to CAD, open real models or use a network client.

Set `ApplicationOutputDirectory` explicitly to the independent application output containing the DLL and Config directory. Send BaseOutputPath, BaseIntermediateOutputPath and MSBuildProjectExtensionsPath to a separate build output directory. Restore with a local package source and NuGetAudit=false, build with --no-restore, then execute UpperChordTransactionChecks.dll.

Coverage: exact two-part linked plan plus eight main dimensions; each missing linked part or dimension; unknown configuration; external probe path; ambiguous dimensions; absolute compensation; missing main mapping; production transaction coordinator write/rebuild/readback/save failures and verified/unverified rollback. The coordinator is invoked from the loaded production DLL; only CAD operations are substitutes.

Does not prove real WPF interaction, SolidWorks COM compatibility, actual component configuration/reference boundaries, geometry rebuild, on-disk CAD values after reopen, or real rollback.
