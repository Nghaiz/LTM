using System;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>A fixed-capacity journal of local weapon commands awaiting server acknowledgement.</summary>
    public sealed class PredictedWeaponCommandBuffer
    {
        public const int Capacity = 32;

        private readonly PredictedWeaponCommand[] _commands =
            new PredictedWeaponCommand[Capacity];
        private int _count;

        public int Count => _count;
        public PredictedWeaponCommand this[int index]
            => index >= 0 && index < _count
                ? _commands[index]
                : throw new ArgumentOutOfRangeException(nameof(index));

        public void Add(uint inputTick, uint localTick, in Vec3 aim)
        {
            if (_count == Capacity)
            {
                Array.Copy(_commands, 1, _commands, 0, Capacity - 1);
                _count--;
            }

            _commands[_count++] = new PredictedWeaponCommand(inputTick, localTick, in aim);
        }

        public void RemoveAcknowledged(uint acknowledgedInputTick)
        {
            int write = 0;
            for (int read = 0; read < _count; read++)
            {
                PredictedWeaponCommand command = _commands[read];
                if (!IsNewer(command.InputTick, acknowledgedInputTick)) continue;
                _commands[write++] = command;
            }

            _count = write;
        }

        public void Clear() => _count = 0;

        private static bool IsNewer(uint value, uint baseline)
            => unchecked((int)(value - baseline)) > 0;
    }

    public readonly struct PredictedWeaponCommand
    {
        internal PredictedWeaponCommand(uint inputTick, uint localTick, in Vec3 aim)
        {
            InputTick = inputTick;
            LocalTick = localTick;
            Aim = aim;
        }

        public uint InputTick { get; }
        public uint LocalTick { get; }
        public Vec3 Aim { get; }
    }
}
