using System.Text.RegularExpressions;

namespace DcAnim.Core.Catalog;

/// <summary>
/// Assigns hero animation IDs to <see cref="AnimationCategory"/> families using
/// verified ID naming conventions from <c>atlas/beheaded.atlas</c> (572 IDs).
/// Rules are intentionally conservative and documented; ambiguous IDs fall
/// into <see cref="AnimationCategory.Other"/>. Precedence: Idle, then
/// Attacks, then Movement - e.g. <c>idleGunA</c> is a stance (Idle), while
/// <c>blowgunAtkShoot</c> is an attack even though it implies locomotion-free
/// aiming.
/// <para/>
/// Verified examples (stock beheaded catalog):
/// <list type="bullet">
/// <item>Attacks: <c>AtkKatanaB2</c>, <c>combAtkA</c>, <c>gunShootA</c>,
/// <c>blowgunAtkShoot</c>, <c>HeavyBowShoot</c>, <c>rapierCombo</c>,
/// <c>dualAxeSequence</c>, <c>lethalHit</c>, <c>castA</c></item>
/// <item>Idle: <c>idle</c>, <c>idleKnee</c>, <c>idleGunA</c>, <c>climbIdleA</c>,
/// <c>travoltaIdle</c></item>
/// <item>Movement: <c>run</c>, <c>dash</c>, <c>wallRun</c>, <c>wallClimbA</c>,
/// <c>teleport</c>, <c>crouch</c></item>
/// <item>Other: <c>blockRampart</c> (defensive shield poses),
/// <c>dead</c>, <c>openChest</c>, <c>think</c>, <c>sit</c>,
/// <c>richterMode_tackle_loop</c> (crossover set)</item>
/// </list>
/// </summary>
public static partial class AnimationCategorizer
{
    [GeneratedRegex("(atk|shoot|kick|punch|throw|bow|gun|cast|combo|halberd|sequence|reload|lethal)", RegexOptions.IgnoreCase)]
    private static partial Regex AttackPattern();

    [GeneratedRegex("idle", RegexOptions.IgnoreCase)]
    private static partial Regex IdlePattern();

    [GeneratedRegex("^(run|walk|dash|roll|jump|fall|land|climb|swim|dodge|vault|slide|crouch|fly|stairs|volte|wall|teleport|enddash|endclimb)", RegexOptions.IgnoreCase)]
    private static partial Regex MovementPattern();

    public static AnimationCategory Categorize(string animationId)
    {
        if (string.IsNullOrWhiteSpace(animationId))
            return AnimationCategory.Other;
        if (IdlePattern().IsMatch(animationId))
            return AnimationCategory.Idle;
        if (AttackPattern().IsMatch(animationId))
            return AnimationCategory.Attacks;
        if (MovementPattern().IsMatch(animationId))
            return AnimationCategory.Movement;
        return AnimationCategory.Other;
    }

    public static string DisplayName(AnimationCategory category) => category switch
    {
        AnimationCategory.Attacks => "Attacks",
        AnimationCategory.Movement => "Movement",
        AnimationCategory.Idle => "Idle",
        AnimationCategory.Other => "Other",
        _ => category.ToString(),
    };
}
