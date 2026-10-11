using FreeXplorer.Fatx;
using FreeXplorer.Storage;

namespace FreeXplorer.Tests;

// Throws random operations at a volume and a plain in-memory model of the same tree,
// reopening the volume now and then, and checks they never disagree.
// FREEXPLORER_FUZZ_STEPS and FREEXPLORER_FUZZ_SEEDS turn the dial up for a longer run.
public class FuzzTests
{
    static int Steps => int.TryParse(Environment.GetEnvironmentVariable("FREEXPLORER_FUZZ_STEPS"), out int n) ? n : 1500;

    public static IEnumerable<object[]> Seeds()
    {
        int count = int.TryParse(Environment.GetEnvironmentVariable("FREEXPLORER_FUZZ_SEEDS"), out int n) ? n : 6;
        for (int seed = 1; seed <= count; seed++)
            yield return new object[] { seed, seed % 2 == 0 };
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_operations_match_the_model(int seed, bool big)
    {
        // the big one has 4K clusters and a 32 bit FAT, the small one 16K clusters and a 16 bit FAT
        var (dev, vol) = big ? TestVolumes.Create(2L << 30, 8) : TestVolumes.Create(96L << 20);
        var model = new FuzzModel();
        var rng = new Random(seed);

        for (int step = 1; step <= Steps; step++)
        {
            FuzzDriver.Step(vol, model, rng);

            if (step % 150 == 0)
                FuzzDriver.Compare(vol, model, $"seed {seed}, step {step}");
            if (step % 500 == 0)
            {
                vol = TestVolumes.Reopen(dev, vol);
                FuzzDriver.Compare(vol, model, $"seed {seed}, reopened at step {step}");
            }
        }

        TestVolumes.AssertClean(vol);
    }
}
