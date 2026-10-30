using Xunit;

public class UnitTests {
    [Fact] public void PasswordHashIsSaltedAndVerifies() {
        var first = Passwords.Hash("test-password-1234");
        var second = Passwords.Hash("test-password-1234");
        Assert.NotEqual(first, second);
        Assert.True(Passwords.Verify("test-password-1234", first));
        Assert.False(Passwords.Verify("wrong-password", first));
    }
    [Fact] public async Task RetryServiceRecoversFromTransientFailure() {
        var service = new TransientRetryService();
        var calls = 0;
        var result = await service.ExecuteAsync<string>(_ => {
            calls++;
            if (calls == 1) throw new HttpRequestException("transient");
            return Task.FromResult("ok");
        }, CancellationToken.None);
        Assert.Equal("ok", result);
        Assert.Equal(2, calls);
    }
    [Fact] public async Task RetryServiceDoesNotRetryNonTransientFailures() {
        var service = new TransientRetryService();
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await service.ExecuteAsync<string>(_ => {
            calls++; throw new InvalidOperationException("not transient");
        }, CancellationToken.None));
        Assert.Equal(1, calls);
    }
}
