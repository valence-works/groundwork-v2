using Groundwork.Kernel;

namespace Groundwork.Substrate.Relational;

/// <summary>
/// Internal test seam for proving that provider disposal and legacy-session registration are atomic.
/// </summary>
internal interface ISessionRegistrationObserver : IProviderCommandObserver
{
    void OnSessionRegistrationEligibilityChecked();

    void OnProviderDisposalAttempted();
}
