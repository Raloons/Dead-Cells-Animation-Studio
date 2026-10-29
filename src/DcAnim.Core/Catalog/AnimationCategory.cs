namespace DcAnim.Core.Catalog;

/// <summary>
/// Broad animation families derived from hero animation ID naming patterns.
/// This is a browsing aid, not a native classification: the game ships no
/// category metadata, so membership follows documented ID conventions
/// (see <see cref="AnimationCategorizer"/>). Stock beheaded catalog splits as
/// 388 Attacks, 31 Movement, 50 Idle, 103 Other. Anything ambiguous lands in
/// <see cref="Other"/> rather than being guessed.
/// </summary>
public enum AnimationCategory
{
    Attacks,
    Movement,
    Idle,
    Other,
}
