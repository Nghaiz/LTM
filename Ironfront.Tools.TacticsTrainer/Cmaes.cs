using System;

namespace Ironfront.Tools.TacticsTrainer
{
    /// <summary>
    /// The covariance matrix adaptation evolution strategy, (mu/mu_w, lambda)-CMA-ES, with the
    /// default strategy parameters of Hansen's tutorial ("The CMA Evolution Strategy: A Tutorial",
    /// arXiv:1604.00772, table 1). Phase P29.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it replaced P28's strategy.</b> P28 moved every weight with the same step, shrunk on a
    /// fixed schedule (0.97 a generation) whatever the search was finding. CMA-ES learns both: the
    /// step size from how far successive steps travel (cumulative step-size adaptation), and the
    /// shape of the search from which directions paid (rank-one and rank-mu covariance updates), so
    /// correlated weights -- the garrison and the defence share, the gathering distance and share --
    /// are moved together.
    /// </para>
    /// <para>
    /// Deterministic for its random source. Plain doubles and a Jacobi eigensolver: for the thirty
    /// or so weights a commander profile has, nothing heavier is needed.
    /// </para>
    /// </remarks>
    public sealed class Cmaes
    {
        private readonly int _n;
        private readonly int _lambda;
        private readonly int _mu;
        private readonly double[] _weights;
        private readonly double _mueff;
        private readonly double _cc, _cs, _c1, _cmu, _damps, _chiN;

        private readonly double[] _mean;
        private readonly double[] _pc;
        private readonly double[] _ps;
        private readonly double[,] _c;
        private readonly double[,] _b;
        private readonly double[] _d;
        private readonly double[][] _z;
        private readonly double[][] _x;
        private double _sigma;
        private int _generation;
        private int _eigenGeneration;

        public Cmaes(float[] start, double sigma, int lambda)
        {
            _n = start.Length;
            _lambda = Math.Max(4, lambda);
            _mu = _lambda / 2;

            _weights = new double[_mu];
            double sum = 0;
            for (int i = 0; i < _mu; i++)
            {
                _weights[i] = Math.Log(_mu + 0.5) - Math.Log(i + 1);
                sum += _weights[i];
            }
            double sumSq = 0;
            for (int i = 0; i < _mu; i++)
            {
                _weights[i] /= sum;
                sumSq += _weights[i] * _weights[i];
            }
            _mueff = 1.0 / sumSq;

            _cc = (4 + _mueff / _n) / (_n + 4 + 2 * _mueff / _n);
            _cs = (_mueff + 2) / (_n + _mueff + 5);
            _c1 = 2 / ((_n + 1.3) * (_n + 1.3) + _mueff);
            _cmu = Math.Min(1 - _c1, 2 * (_mueff - 2 + 1 / _mueff) / ((_n + 2) * (_n + 2) + _mueff));
            _damps = 1 + 2 * Math.Max(0, Math.Sqrt((_mueff - 1) / (_n + 1)) - 1) + _cs;
            _chiN = Math.Sqrt(_n) * (1 - 1.0 / (4 * _n) + 1.0 / (21.0 * _n * _n));

            _mean = new double[_n];
            for (int i = 0; i < _n; i++) _mean[i] = start[i];
            _pc = new double[_n];
            _ps = new double[_n];
            _c = new double[_n, _n];
            _b = new double[_n, _n];
            _d = new double[_n];
            for (int i = 0; i < _n; i++)
            {
                _c[i, i] = 1;
                _b[i, i] = 1;
                _d[i] = 1;
            }
            _z = new double[_lambda][];
            _x = new double[_lambda][];
            for (int k = 0; k < _lambda; k++)
            {
                _z[k] = new double[_n];
                _x[k] = new double[_n];
            }
            _sigma = sigma;
        }

        public int Lambda => _lambda;

        public double Sigma => _sigma;

        /// <summary>The distribution's mean: the recombined best of the last generation.</summary>
        public float[] Mean
        {
            get
            {
                var m = new float[_n];
                for (int i = 0; i < _n; i++) m[i] = (float)_mean[i];
                return m;
            }
        }

        /// <summary>A new generation of <see cref="Lambda"/> points, drawn from <paramref name="rng"/>.</summary>
        public double[][] Ask(SimRandom rng)
        {
            for (int k = 0; k < _lambda; k++)
            {
                for (int i = 0; i < _n; i++) _z[k][i] = rng.NextGaussian();
                for (int i = 0; i < _n; i++)
                {
                    double y = 0;
                    for (int j = 0; j < _n; j++) y += _b[i, j] * _d[j] * _z[k][j];
                    _x[k][i] = _mean[i] + _sigma * y;
                }
            }
            return _x;
        }

        /// <summary>Learns from the generation <see cref="Ask"/> drew, given its points best first.</summary>
        public void Tell(int[] bestFirst)
        {
            _generation++;
            var old = (double[])_mean.Clone();

            for (int i = 0; i < _n; i++)
            {
                double m = 0;
                for (int r = 0; r < _mu; r++) m += _weights[r] * _x[bestFirst[r]][i];
                _mean[i] = m;
            }

            // C^(-1/2) (m - old) / sigma = B diag(1/D) B^T y_w
            var yw = new double[_n];
            for (int i = 0; i < _n; i++) yw[i] = (_mean[i] - old[i]) / _sigma;
            var bty = new double[_n];
            for (int j = 0; j < _n; j++)
            {
                double s = 0;
                for (int i = 0; i < _n; i++) s += _b[i, j] * yw[i];
                bty[j] = s / _d[j];
            }
            double csFactor = Math.Sqrt(_cs * (2 - _cs) * _mueff);
            double psNorm = 0;
            for (int i = 0; i < _n; i++)
            {
                double s = 0;
                for (int j = 0; j < _n; j++) s += _b[i, j] * bty[j];
                _ps[i] = (1 - _cs) * _ps[i] + csFactor * s;
                psNorm += _ps[i] * _ps[i];
            }
            psNorm = Math.Sqrt(psNorm);

            bool hsig = psNorm / Math.Sqrt(1 - Math.Pow(1 - _cs, 2 * _generation)) / _chiN < 1.4 + 2.0 / (_n + 1);
            double ccFactor = Math.Sqrt(_cc * (2 - _cc) * _mueff);
            for (int i = 0; i < _n; i++) _pc[i] = (1 - _cc) * _pc[i] + (hsig ? ccFactor * yw[i] : 0);

            double keep = 1 - _c1 - _cmu + (hsig ? 0 : _c1 * _cc * (2 - _cc));
            for (int i = 0; i < _n; i++)
            {
                for (int j = 0; j <= i; j++)
                {
                    double rankMu = 0;
                    for (int r = 0; r < _mu; r++)
                    {
                        double[] x = _x[bestFirst[r]];
                        rankMu += _weights[r] * (x[i] - old[i]) * (x[j] - old[j]);
                    }
                    double value = keep * _c[i, j] + _c1 * _pc[i] * _pc[j] + _cmu * rankMu / (_sigma * _sigma);
                    _c[i, j] = value;
                    _c[j, i] = value;
                }
            }

            _sigma *= Math.Exp(_cs / _damps * (psNorm / _chiN - 1));
            _sigma = Math.Clamp(_sigma, 1e-4, 1.0);

            // Decompose every few generations, as the tutorial suggests, to keep the O(n^3) step rare.
            if (_generation - _eigenGeneration > _lambda / (_c1 + _cmu) / _n / 10)
            {
                _eigenGeneration = _generation;
                Eigen();
            }
        }

        /// <summary>B and D from C = B diag(D^2) B^T, by cyclic Jacobi rotations.</summary>
        private void Eigen()
        {
            var a = (double[,])_c.Clone();
            var v = new double[_n, _n];
            for (int i = 0; i < _n; i++) v[i, i] = 1;

            for (int sweep = 0; sweep < 100; sweep++)
            {
                double off = 0;
                for (int p = 0; p < _n; p++)
                    for (int q = p + 1; q < _n; q++)
                        off += a[p, q] * a[p, q];
                if (off < 1e-22) break;

                for (int p = 0; p < _n; p++)
                {
                    for (int q = p + 1; q < _n; q++)
                    {
                        if (Math.Abs(a[p, q]) < 1e-300) continue;
                        double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                        double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        if (theta == 0) t = 1;
                        double c = 1 / Math.Sqrt(t * t + 1);
                        double s = t * c;

                        for (int k = 0; k < _n; k++)
                        {
                            double akp = a[k, p], akq = a[k, q];
                            a[k, p] = c * akp - s * akq;
                            a[k, q] = s * akp + c * akq;
                        }
                        for (int k = 0; k < _n; k++)
                        {
                            double apk = a[p, k], aqk = a[q, k];
                            a[p, k] = c * apk - s * aqk;
                            a[q, k] = s * apk + c * aqk;
                        }
                        for (int k = 0; k < _n; k++)
                        {
                            double vkp = v[k, p], vkq = v[k, q];
                            v[k, p] = c * vkp - s * vkq;
                            v[k, q] = s * vkp + c * vkq;
                        }
                    }
                }
            }

            for (int i = 0; i < _n; i++)
            {
                _d[i] = Math.Sqrt(Math.Max(a[i, i], 1e-20));
                for (int j = 0; j < _n; j++) _b[j, i] = v[j, i];
            }
        }
    }
}
