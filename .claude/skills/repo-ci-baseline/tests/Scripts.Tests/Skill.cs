using System.Reflection;

// The skill's folder (the project's SkillDir metadata), for the files a test reads beside the scripts.
static class Skill
{
	public static readonly string Dir = typeof(Skill).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "SkillDir").Value!;

	public static string Path(params string[] parts) => System.IO.Path.Combine([Dir, .. parts]);

	// The Template's Owned workflow of that name.
	public static string Workflow(string name) => File.ReadAllText(Path("template", "owned", ".github", "workflows", name));
}
