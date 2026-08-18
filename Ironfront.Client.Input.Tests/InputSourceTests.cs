using Ironfront.Net.Protocol;
using Ironfront.Net.Unity;
using Xunit;

namespace Ironfront.Client.Input.Tests
{
    /// <summary>
    /// The half of the phase-00 task-3 input seam that can be executed without Unity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What these tests are for: the bit assignment. It is the one part of the seam that can be
    /// wrong while everything still compiles and the game still runs — a swapped Aim and Reload
    /// bit produces a client whose reload key aims, on the server only, and nothing anywhere
    /// says so. Every other risk in step 02 is a transcription risk inside
    /// <c>FpsActorController</c>, which no test can reach and which <c>InputShadowCompare</c>
    /// covers at runtime instead.
    /// </para>
    /// <para>
    /// The bit numbers are never written down here. Asserting <c>Fire == 1 &lt;&lt; 0</c> would
    /// only prove this file agrees with itself; asserting against
    /// <see cref="InputButtons"/> proves the packer agrees with protocol-spec.md § 4.2, which is
    /// the property that matters.
    /// </para>
    /// </remarks>
    public class InputButtonPackerTests
    {
        [Theory]
        [InlineData(InputButtons.Fire)]
        [InlineData(InputButtons.Aim)]
        [InlineData(InputButtons.Reload)]
        [InlineData(InputButtons.Jump)]
        [InlineData(InputButtons.Crouch)]
        [InlineData(InputButtons.Sprint)]
        [InlineData(InputButtons.Use)]
        public void One_pressed_button_sets_exactly_its_own_bit(InputButtons expected)
        {
            ushort packed = InputButtonPacker.Pack(
                fire:   expected == InputButtons.Fire,
                aim:    expected == InputButtons.Aim,
                reload: expected == InputButtons.Reload,
                jump:   expected == InputButtons.Jump,
                crouch: expected == InputButtons.Crouch,
                sprint: expected == InputButtons.Sprint,
                use:    expected == InputButtons.Use);

            Assert.Equal((ushort)expected, packed);
        }

        [Fact]
        public void Nothing_pressed_packs_to_zero()
        {
            Assert.Equal(0, InputButtonPacker.Pack(false, false, false, false, false, false, false));
        }

        [Fact]
        public void Every_button_pressed_packs_to_their_union_and_nothing_else()
        {
            const InputButtons all = InputButtons.Fire | InputButtons.Aim | InputButtons.Reload
                                     | InputButtons.Jump | InputButtons.Crouch
                                     | InputButtons.Sprint | InputButtons.Use;

            Assert.Equal((ushort)all,
                InputButtonPacker.Pack(true, true, true, true, true, true, true));
        }

        /// <summary>
        /// The bits the packer deliberately never sets. If one of these starts being packed
        /// without <c>InputButtonPacker</c>'s remarks being updated, that is the change to
        /// question — a bit that gains a writer without gaining a reader is how a protocol field
        /// becomes permanently ambiguous.
        /// </summary>
        [Fact]
        public void Bits_with_no_producer_in_FpsActorController_stay_clear()
        {
            const InputButtons unused = InputButtons.Prone | InputButtons.ThrowGrenade
                                        | InputButtons.LeanLeft | InputButtons.LeanRight
                                        | InputButtons.SwitchWeapon0 | InputButtons.SwitchWeapon1
                                        | InputButtons.SwitchWeapon2 | InputButtons.SwitchWeapon3;

            ushort packed = InputButtonPacker.Pack(true, true, true, true, true, true, true);

            Assert.Equal(0, packed & (ushort)unused);
        }
    }

    /// <summary>
    /// The read half. Together with <see cref="InputButtonPackerTests"/> this is a round trip:
    /// what the local source writes is what a consumer reads.
    /// </summary>
    public class InputSourceExtensionsTests
    {
        private sealed class FixedButtons : IInputSource
        {
            public FixedButtons(ushort buttons) => Buttons = buttons;

            public float MoveX => 0f;
            public float MoveZ => 0f;
            public float Yaw => 0f;
            public float Pitch => 0f;
            public float Lean => 0f;
            public float LookDeltaX => 0f;
            public float LookDeltaY => 0f;
            public ushort Buttons { get; }
            public float HeliYaw => 0f;
            public float HeliCollective => 0f;
            public float HeliRoll => 0f;
            public float HeliPitch => 0f;
        }

        [Fact]
        public void Each_accessor_reads_back_exactly_the_button_that_was_packed()
        {
            Assert.True(new FixedButtons(InputButtonPacker.Pack(
                fire: true, aim: false, reload: false, jump: false,
                crouch: false, sprint: false, use: false)).Fire());

            Assert.True(new FixedButtons(InputButtonPacker.Pack(
                fire: false, aim: true, reload: false, jump: false,
                crouch: false, sprint: false, use: false)).Aim());

            Assert.True(new FixedButtons(InputButtonPacker.Pack(
                fire: false, aim: false, reload: true, jump: false,
                crouch: false, sprint: false, use: false)).Reload());

            Assert.True(new FixedButtons(InputButtonPacker.Pack(
                fire: false, aim: false, reload: false, jump: true,
                crouch: false, sprint: false, use: false)).Jump());

            Assert.True(new FixedButtons(InputButtonPacker.Pack(
                fire: false, aim: false, reload: false, jump: false,
                crouch: true, sprint: false, use: false)).Crouch());

            Assert.True(new FixedButtons(InputButtonPacker.Pack(
                fire: false, aim: false, reload: false, jump: false,
                crouch: false, sprint: true, use: false)).Sprint());

            Assert.True(new FixedButtons(InputButtonPacker.Pack(
                fire: false, aim: false, reload: false, jump: false,
                crouch: false, sprint: false, use: true)).Use());
        }

        [Fact]
        public void One_button_pressed_does_not_read_as_any_other()
        {
            var onlyAim = new FixedButtons((ushort)InputButtons.Aim);

            Assert.True(onlyAim.Aim());
            Assert.False(onlyAim.Fire());
            Assert.False(onlyAim.Reload());
            Assert.False(onlyAim.Jump());
            Assert.False(onlyAim.Crouch());
            Assert.False(onlyAim.Sprint());
            Assert.False(onlyAim.Use());
        }

        /// <summary>
        /// A null source reads as nothing pressed rather than throwing. The accessors sit on
        /// per-frame paths inside <c>Actor.Update</c>; a source that can throw turns one missed
        /// assignment into an exception every frame for the rest of the session.
        /// </summary>
        [Fact]
        public void A_null_source_reads_as_nothing_pressed()
        {
            // null! because this project has nullable reference types on and Unity does not.
            // The guard being tested exists precisely for the Unity side, where the compiler
            // offers no such warning and a field can genuinely be null at runtime.
            IInputSource none = null!;

            Assert.False(none.Fire());
            Assert.False(none.Sprint());
        }
    }

    public class NullInputSourceTests
    {
        [Fact]
        public void Reports_no_movement_no_look_and_no_buttons()
        {
            IInputSource s = NullInputSource.Instance;

            Assert.Equal(0f, s.MoveX);
            Assert.Equal(0f, s.MoveZ);
            Assert.Equal(0f, s.Yaw);
            Assert.Equal(0f, s.Pitch);
            Assert.Equal(0f, s.Lean);
            Assert.Equal(0f, s.LookDeltaX);
            Assert.Equal(0f, s.LookDeltaY);
            Assert.Equal(0, s.Buttons);
        }
    }

    public class NetInputSourceTests
    {
        private static NetInputSource WithFrame(
            float moveX, float moveZ, float yaw, float pitch, InputButtons buttons)
        {
            var source = new NetInputSource();
            source.SetFrame(InputFrame.FromFloats(moveX, moveZ, yaw, pitch, buttons));
            return source;
        }

        /// <summary>
        /// The tolerances are the quantization steps the protocol chose, not slack: the move
        /// axes are i8 over -1..1 (1/127 per step) and yaw is u16 over 360° (~0.0055° per step).
        /// A test that asserted exact equality here would be asserting something false.
        /// </summary>
        [Fact]
        public void Axes_survive_the_wire_within_one_quantization_step()
        {
            NetInputSource s = WithFrame(0.37f, -0.62f, 271.4f, -33.2f, InputButtons.None);

            Assert.Equal(0.37f, s.MoveX, 1f / 127f);
            Assert.Equal(-0.62f, s.MoveZ, 1f / 127f);
            Assert.Equal(271.4f, s.Yaw, 0.01f);
            Assert.Equal(-33.2f, s.Pitch, 0.01f);
        }

        [Fact]
        public void Buttons_pass_through_unchanged()
        {
            NetInputSource s = WithFrame(
                0f, 0f, 0f, 0f, InputButtons.Fire | InputButtons.Sprint);

            Assert.True(s.Fire());
            Assert.True(s.Sprint());
            Assert.False(s.Aim());
        }

        [Theory]
        [InlineData(InputButtons.None, 0f)]
        [InlineData(InputButtons.LeanLeft, -1f)]
        [InlineData(InputButtons.LeanRight, 1f)]
        [InlineData(InputButtons.LeanLeft | InputButtons.LeanRight, 0f)]
        public void Lean_is_tri_state_because_the_wire_carries_bits_not_an_axis(
            InputButtons buttons, float expected)
        {
            Assert.Equal(expected, WithFrame(0f, 0f, 0f, 0f, buttons).Lean);
        }

        /// <summary>
        /// There is no mouse at the far end of a socket. Returning a plausible-looking non-zero
        /// delta would make helicopter control drift for a remote pilot with nothing to blame.
        /// </summary>
        [Fact]
        public void There_is_no_mouse_delta_over_the_network()
        {
            NetInputSource s = WithFrame(1f, 1f, 90f, 45f, InputButtons.Fire);

            Assert.Equal(0f, s.LookDeltaX);
            Assert.Equal(0f, s.LookDeltaY);
        }

        [Fact]
        public void A_source_that_was_never_given_a_frame_reports_nothing()
        {
            IInputSource s = new NetInputSource();

            Assert.Equal(0f, s.MoveX);
            Assert.Equal(0f, s.Lean);
            Assert.Equal(0, s.Buttons);
        }
    }
}
