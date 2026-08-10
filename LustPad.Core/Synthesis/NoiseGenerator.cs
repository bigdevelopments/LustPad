namespace LustPad.Core.Synthesis;

/// <summary>
/// Seeded stereo noise: white, pink (Paul Kellet), or brown (leaky integrator).
/// Independent L/R streams for width without a mono image.
/// </summary>
internal sealed class NoiseGenerator
{
    private readonly Random _rngL;
    private readonly Random _rngR;
    private readonly PinkState _pinkL = new();
    private readonly PinkState _pinkR = new();
    private float _brownL;
    private float _brownR;

    public NoiseGenerator(int seed)
    {
        _rngL = new Random(seed ^ unchecked((int)0xA5A5A5A5));
        _rngR = new Random(seed ^ unchecked((int)0x5A5A5A5A));
    }

    public (float left, float right) Next(NoiseType type)
    {
        return type switch
        {
            NoiseType.Pink => (_pinkL.Next(White(_rngL)), _pinkR.Next(White(_rngR))),
            NoiseType.Brown => (NextBrown(ref _brownL, _rngL), NextBrown(ref _brownR, _rngR)),
            _ => (White(_rngL), White(_rngR)),
        };
    }

    private static float White(Random rng) => (float)(rng.NextDouble() * 2.0 - 1.0);

    private static float NextBrown(ref float state, Random rng)
    {
        // Leaky integrator — darker "rumble" noise
        state = (state + White(rng) * 0.02f) * 0.998f;
        state = Math.Clamp(state, -1f, 1f);
        return state * 3.5f; // makeup
    }

    /// <summary>Paul Kellet's refined pink noise filter (approx -3 dB/oct).</summary>
    private sealed class PinkState
    {
        private float _b0, _b1, _b2, _b3, _b4, _b5, _b6;

        public float Next(float white)
        {
            _b0 = 0.99886f * _b0 + white * 0.0555179f;
            _b1 = 0.99332f * _b1 + white * 0.0750759f;
            _b2 = 0.96900f * _b2 + white * 0.1538520f;
            _b3 = 0.86650f * _b3 + white * 0.3104856f;
            _b4 = 0.55000f * _b4 + white * 0.5329522f;
            _b5 = -0.7616f * _b5 - white * 0.0168980f;
            float pink = _b0 + _b1 + _b2 + _b3 + _b4 + _b5 + _b6 + white * 0.5362f;
            _b6 = white * 0.115926f;
            return pink * 0.11f; // scale into ~[-1,1]
        }
    }
}
