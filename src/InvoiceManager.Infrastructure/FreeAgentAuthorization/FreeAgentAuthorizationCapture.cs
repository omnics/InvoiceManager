using InvoiceManager.Core.Integrations.FreeAgent;

namespace InvoiceManager.Infrastructure.FreeAgentAuthorization;

/// <summary>
/// Persists and clears a FreeAgent account's refresh token/subdomain pair together, safely:
/// <see cref="SaveAsync"/> clears any subdomain left over from a previously-authorized account
/// before touching either value, so every failure from that point on degrades to either the
/// untouched previous authorization (if the clear itself fails) or a "refresh token present,
/// subdomain not yet known" state (if either save fails) - never a refresh token paired with a
/// stale, wrong-account subdomain *from a failure within this class* (see the concurrent-rotation
/// caveat below for a narrower, different-shaped race this doesn't cover). The latter state is
/// exactly what
/// <c>AuthorizationModel.IsFreeAgentSubdomainMissing</c> already detects and surfaces as a
/// warning, so no new failure mode needs its own handling. <see cref="ClearAsync"/> (used when
/// an administrator resets FreeAgent authorization) clears in the same subdomain-first order,
/// for the same reason.
///
/// <para>
/// A single in-process <see cref="SemaphoreSlim"/> serialises every call through this class -
/// <see cref="IFreeAgentAuthorizationStore"/> does not otherwise serialise its own multi-step
/// operations, so two authorization callbacks completing concurrently on the same AdminWeb
/// instance (two browser tabs, or two administrators) could otherwise interleave their
/// individual reads/writes into a cross-account pair even though every individual call
/// succeeded. This does not protect against concurrent writes from two different AdminWeb
/// instances, but this authorization flow is a rare, manual, single-operator action, not a hot
/// path worth a distributed lock for.
/// </para>
///
/// <para>
/// Not covered: <c>FreeAgentTokenProvider.RefreshAsync</c> also writes a rotated refresh token
/// straight to <see cref="IFreeAgentAuthorizationStore"/>, outside this gate, whenever an
/// expired cached access token is next needed (e.g. from
/// <c>FreeAgentAuthorizationHealthCheck</c>, on any process that runs one - both AdminWeb and
/// Functions do). If that rotation's read-refresh-write round trip happens to span a
/// <see cref="SaveAsync"/> for a different account, the rotated token can still land after and
/// overwrite the newly-saved one, producing the same cross-account pair this class exists to
/// prevent - gating that write wouldn't fully close this, since the rotation already committed
/// to persisting a token it read before the account switch, not just racing on when it writes.
/// The equivalent race already exists between Functions' independent rotation and any
/// AdminWeb-side capture regardless (a different process entirely, so no in-process gate could
/// ever cover it), so this is accepted as a known, narrow, pre-existing limitation rather than
/// solved here.
/// </para>
/// </summary>
public static class FreeAgentAuthorizationCapture
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task SaveAsync(
        IFreeAgentAuthorizationStore store,
        string refreshToken,
        FreeAgentCompany company,
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            await store.ClearSubdomainAsync(cancellationToken);
            await store.SaveRefreshTokenAsync(refreshToken, cancellationToken);
            await store.SaveSubdomainAsync(company.Subdomain, cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task ClearAsync(IFreeAgentAuthorizationStore store, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            await store.ClearSubdomainAsync(cancellationToken);
            await store.ClearRefreshTokenAsync(cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }
}
