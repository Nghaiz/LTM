using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>Why a delayed throwable reservation was refused.</summary>
    public enum ThrowableRejection : byte
    {
        None = 0,
        NotDelayed = 1,
        AlreadyPending = 2,
        Holstered = 3,
        Reloading = 4,
        NoAmmo = 5,
        OnCooldown = 6,
    }

    /// <summary>
    /// Result of advancing a delayed release. It contains enough information to undo the
    /// inventory commit when the engine cannot create the projectile.
    /// </summary>
    public readonly struct ThrowableTransition
    {
        internal ThrowableTransition(
            bool released, in Vec3 aim, in WeaponRuntimeState beforeState,
            int reserveTaken, int reserveCap)
        {
            Released = released;
            Aim = aim;
            BeforeState = beforeState;
            ReserveTaken = reserveTaken;
            ReserveCap = reserveCap;
        }

        public bool Released { get; }
        public Vec3 Aim { get; }
        internal WeaponRuntimeState BeforeState { get; }
        internal int ReserveTaken { get; }
        internal int ReserveCap { get; }
    }

    /// <summary>
    /// Engine-free authority for carried weapons whose object leaves the hand after an authored
    /// animation delay. Acceptance reserves one use; release commits it exactly once.
    /// </summary>
    public static class ThrowableLifecycle
    {
        public static ThrowableRejection TryBegin(
            ref WeaponRuntimeState state, in WeaponConfig config,
            uint inputTick, uint serverTick, in Vec3 aim)
        {
            if (!config.HasDelayedRelease) return ThrowableRejection.NotDelayed;
            if (state.PendingRelease) return ThrowableRejection.AlreadyPending;
            if (!state.Unholstered) return ThrowableRejection.Holstered;
            if (state.Reloading) return ThrowableRejection.Reloading;
            if (state.AmmoInClip == 0) return ThrowableRejection.NoAmmo;

            uint cooldownTicks = (uint)Math.Ceiling(config.Cooldown * ProtocolConstants.SIM_TICK_RATE);
            if (state.HasThrowableReleaseTick &&
                !HasReached(serverTick, state.LastThrowableReleaseTick + cooldownTicks))
                return ThrowableRejection.OnCooldown;

            state.PendingRelease = true;
            state.PendingReleaseTick = serverTick + config.ReleaseDelayTicks;
            state.PendingInputTick = inputTick;
            state.PendingAim = aim;
            return ThrowableRejection.None;
        }

        public static ThrowableTransition TryRelease(
            ref WeaponRuntimeState state, in WeaponConfig config,
            uint serverTick, in ActorAmmoSource ammo)
        {
            if (!state.PendingRelease || !HasReached(serverTick, state.PendingReleaseTick))
                return default;

            WeaponRuntimeState before = state;
            Vec3 aim = state.PendingAim;

            state.PendingRelease = false;
            state.PendingReleaseTick = 0;
            state.PendingInputTick = 0;
            state.PendingAim = Vec3.Zero;

            if (config.SpendsAmmo && state.AmmoInClip > 0)
                state.AmmoInClip--;

            int wanted = config.ClipSize - state.AmmoInClip;
            int reserveTaken = wanted > 0 ? ammo.Take(ref state, wanted) : 0;
            state.AmmoInClip = (byte)(state.AmmoInClip + reserveTaken);
            state.LastFiredTime = serverTick / (float)ProtocolConstants.SIM_TICK_RATE;
            state.LastThrowableReleaseTick = serverTick;
            state.HasThrowableReleaseTick = true;

            int reserveCap = config.SpareAmmo > 0 ? config.SpareAmmo : 0;
            return new ThrowableTransition(true, in aim, in before, reserveTaken, reserveCap);
        }

        public static bool Cancel(ref WeaponRuntimeState state)
        {
            if (!state.PendingRelease) return false;

            state.PendingRelease = false;
            state.PendingReleaseTick = 0;
            state.PendingInputTick = 0;
            state.PendingAim = Vec3.Zero;
            return true;
        }

        public static void RollbackRelease(
            ref WeaponRuntimeState state, in ThrowableTransition transition,
            in ActorAmmoSource ammo)
        {
            if (!transition.Released) return;

            if (transition.ReserveTaken > 0 && ammo.SlotIsKnown)
            {
                ammo.Pool.Give(
                    ammo.OwnerId, ammo.Slot, ref state,
                    transition.ReserveTaken, transition.ReserveCap);
            }

            state = transition.BeforeState;
            Cancel(ref state);
        }

        private static bool HasReached(uint now, uint target)
            => unchecked((int)(now - target)) >= 0;
    }
}
