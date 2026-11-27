using System.Diagnostics;

namespace Baseline.Tests;

// test_release_post.py's two tests that time the scanning: test_scanning_stays_linear_in_nesting_depth_and_length and the code-finding
// one. They measure wall-clock time, so they run alone, after the parallel tests: beside them, the suite's other tests (and their
// garbage collections, which pause every thread) stretch one run of a pair and not the other, and a linear scan read as quadratic.
[CollectionDefinition(nameof(ReleasePostTimingTests), DisableParallelization = true)]
public sealed class ReleasePostTimingCollection;

[Collection(nameof(ReleasePostTimingTests))]
public sealed class ReleasePostTimingTests
{
	// Time the same shape at two sizes; a quadratic scan takes ~16x longer at 4x the size (measured before the fix), a linear one ~4x.
	// The best of five runs keeps scheduling noise out, and the 10x bound leaves plenty of slack.
	static void AssertLinear(Func<int, string> shape, Action<string> scan)
	{
		double Best(int n)
		{
			var text = shape(n);
			List<double> runs = [];
			for (var i = 0; i < 5; i++)
			{
				var start = Stopwatch.GetTimestamp();
				scan(text);
				runs.Add(Stopwatch.GetElapsedTime(start).TotalSeconds);
			}
			return runs.Min();
		}

		double small = Best(2_000), large = Best(8_000);
		Assert.True(large < Math.Max(small, 0.001) * 10, $"{large} >= {Math.Max(small, 0.001) * 10}");
	}

	[Theory]
	[MemberData(nameof(ReleasePostTests.SectionShapeIndexes), MemberType = typeof(ReleasePostTests))]
	public void Scanning_stays_linear_in_nesting_depth_and_length(int shape) =>
		AssertLinear(ReleasePostTests.Shapes[shape], text => ReleasePost.NestHeadings(text));

	[Theory]
	[MemberData(nameof(ReleasePostTests.CodeFindingShapeIndexes), MemberType = typeof(ReleasePostTests))]
	public void Finding_code_stays_linear(int shape) =>
		AssertLinear(ReleasePostTests.CodeFindingShapes[shape], text =>
		{
			ReleasePost.SanitizeMarkdown(text);
			ReleasePost.RebaseReadmeLinks(text);
		});
}
