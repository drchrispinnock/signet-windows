using Signet.Models;

namespace Signet.ViewModels;

/// <summary>
/// The Send sheet's model. Not ported yet beyond its errors, which the account flows share
/// (Forget and Export ask for passwords and secret keys the same way).
/// </summary>
public sealed class SendViewModel
{
    public abstract class SendError(string message) : Exception(message), IEquatable<SendError>
    {
        public sealed class NoRecipient() : SendError("Enter a valid address or pick one from your list.");
        public sealed class InvalidAmount() : SendError("Enter an amount in tez.");
        public sealed class InsufficientFunds(decimal available) : SendError($"Not enough spendable tez. Available: {AssetBalance.Format(available, "tz")}.");
        public sealed class NoSecretKey() : SendError("Signet has no usable secret key for this account.");
        public sealed class SendingToSelf() : SendError("That is this account's own address.");
        public sealed class PassphraseRequired() : SendError("Enter the password for this account's key.");
        public sealed class NotEstimated() : SendError("The fee has not been estimated yet; go back and try again.");
        public sealed class NotSeenYet(string hash)
            : SendError($"Operation {hash[..Math.Min(12, hash.Length)]}… has not appeared in a block yet. The node may still have it; wait, or send the same signed operation again. It cannot be paid twice.");

        public bool Equals(SendError? other) => other is not null && other.GetType() == GetType() && other.Message == Message;
        public override bool Equals(object? obj) => Equals(obj as SendError);
        public override int GetHashCode() => HashCode.Combine(GetType(), Message);
    }
}
