namespace YobaLog.Core.Admin;

// Plaintext is only ever returned from CreateAsync — surface it to the operator once, then discard.
// Storage keeps only sha256(token) + a 6-char prefix for UI identification.
public sealed record ApiKeyCreated(ApiKeyInfo Info, string Plaintext);
