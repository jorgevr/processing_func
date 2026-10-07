// CI-3b PROOF (throwaway, to be reverted): a trivial 11th passing test so the suite's total goes
// to 11 while exactly one AzuriteRoundTripTests case is forced to skip, netting 10 executed — the
// count floor must NOT fire, only the Azurite-specific branch.
namespace DatasetProcessingFunction.IntegrationTests;

public sealed class CI3bScratchExtraTest
{
    [Fact]
    public void TrivialPass()
    {
        Assert.True(true);
    }
}
