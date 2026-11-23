/*
 * Anak Native CUDA ArchV2 Trainer (RTX 4090 / sm_89 optimized)
 *
 * Supports BOTH:
 *   - SCReLU (act_flags = 0): (16x768 -> HL)x2 -> 8x (2*HL -> 1)
 *   - Pairwise-Mul (act_flags = 1): (16x768 -> HL)x2 -> 8x (HL -> 1)  [a[i] * b[i], 2x fewer output MACs!]
 *
 * Features:
 *   - Factorized FT (ft_bucket + ft_base, folded at export -> 0 runtime cost)
 *   - GPU-side EMA (decay = 0.999) evaluated on validation and exported!
 *   - Fused Forward + WDL MSE Loss + Sparse-Skipping Backward + Fused Adam (weight_decay = 0.0)
 *   - Cosine LR decay with linear warmup + double-buffered async disk streaming
 *   - Direct export to Anak ArchV2 (0x0004) INT16 QA=255, QB=64 binary .nnue
 */

#include <cuda_runtime.h>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <random>
#include <string>
#include <thread>
#include <vector>

#define CUDA_CHECK(call)                                                                          \
    do {                                                                                          \
        cudaError_t err = (call);                                                                 \
        if (err != cudaSuccess) {                                                                 \
            fprintf(stderr, "CUDA Error at %s:%d: %s\n", __FILE__, __LINE__,                      \
                    cudaGetErrorString(err));                                                     \
            exit(1);                                                                              \
        }                                                                                         \
    } while (0)

static constexpr int RAW_FEATURES = 768;
static constexpr int KING_BUCKETS = 16;
static constexpr int FEATURE_COUNT = KING_BUCKETS * RAW_FEATURES; // 12288
static constexpr int OUTPUT_BUCKETS = 8;
static constexpr int V2_QA = 255;
static constexpr int V2_QB = 64;
static constexpr std::uint32_t MAGIC_HEADER = 0x4B414E41u;
static constexpr std::uint32_t FORMAT_VERSION = 1u;
static constexpr std::uint16_t ARCH_V2 = 0x0004u;

#pragma pack(push, 1)
struct V2BinHeader {
    char magic[8];
    std::uint32_t version;
    std::uint32_t rec_size;
    std::uint64_t count;
    char reserved[40];
};

struct V2Record {
    std::uint16_t our_idx[32];
    std::uint16_t their_idx[32];
    std::int16_t score_cp;
    std::uint8_t wdl;
    std::uint8_t output_bucket;
    std::uint8_t piece_count;
    std::uint8_t our_count;
    std::uint8_t their_count;
    std::uint8_t ply;
};
#pragma pack(pop)

static_assert(sizeof(V2BinHeader) == 64, "Header must be 64 bytes");
static_assert(sizeof(V2Record) == 136, "Record must be 136 bytes");

// ============================================================================
// Pairwise-Mul Forward & Backward Kernels (a[i] * b[i], 2x fewer output weights)
// ============================================================================

template <int HL, int BLOCK_DIM>
__global__ void forward_pairwise_kernel(
    const V2Record* __restrict__ batch,
    int batch_size,
    const float* __restrict__ ft_bucket,
    const float* __restrict__ ft_base,
    const float* __restrict__ ft_bias,
    const float* __restrict__ out_weight, // [OUTPUT_BUCKETS * HL] (HL/2 our + HL/2 their)
    const float* __restrict__ out_bias,
    float* __restrict__ acc_our_out,
    float* __restrict__ acc_their_out,
    float* __restrict__ grad_pred_out,
    float* __restrict__ loss_sum_out,
    float wdl_weight,
    float inv_eval_scale,
    bool compute_grad
) {
    constexpr int HALF = HL / 2;
    int pos = blockIdx.x;
    if (pos >= batch_size) return;

    const V2Record& rec = batch[pos];
    int tid = threadIdx.x;

    __shared__ std::uint16_t s_our[32];
    __shared__ std::uint16_t s_their[32];
    __shared__ int s_count;
    __shared__ int s_bucket;
    __shared__ float s_warp_sums[BLOCK_DIM / 32];

    if (tid < 32) {
        s_our[tid] = rec.our_idx[tid];
        s_their[tid] = rec.their_idx[tid];
    }
    if (tid == 0) {
        s_count = rec.our_count;
        s_bucket = rec.output_bucket;
    }
    __syncthreads();

    int count = s_count;
    int bucket = s_bucket;
    const float* ow_our = out_weight + (bucket * HL);
    const float* ow_their = ow_our + HALF;

    float partial = 0.0f;
    float* dst_o = acc_our_out + (static_cast<std::size_t>(pos) * HL);
    float* dst_t = acc_their_out + (static_cast<std::size_t>(pos) * HL);

    #pragma unroll
    for (int j = tid; j < HALF; j += BLOCK_DIM) {
        int jb = j + HALF;
        float ao_a = ft_bias[j], ao_b = ft_bias[jb];
        float at_a = ao_a, at_b = ao_b;
        for (int p = 0; p < count; ++p) {
            int io = s_our[p];
            int it = s_their[p];
            int io_base = io % RAW_FEATURES;
            int it_base = it % RAW_FEATURES;
            ao_a += ft_bucket[io * HL + j] + ft_base[io_base * HL + j];
            ao_b += ft_bucket[io * HL + jb] + ft_base[io_base * HL + jb];
            at_a += ft_bucket[it * HL + j] + ft_base[it_base * HL + j];
            at_b += ft_bucket[it * HL + jb] + ft_base[it_base * HL + jb];
        }
        if (compute_grad) {
            dst_o[j] = ao_a;
            dst_o[jb] = ao_b;
            dst_t[j] = at_a;
            dst_t[jb] = at_b;
        }
        float co_a = fminf(fmaxf(ao_a, 0.0f), 1.0f);
        float co_b = fminf(fmaxf(ao_b, 0.0f), 1.0f);
        float ct_a = fminf(fmaxf(at_a, 0.0f), 1.0f);
        float ct_b = fminf(fmaxf(at_b, 0.0f), 1.0f);
        partial += (co_a * co_b) * ow_our[j] + (ct_a * ct_b) * ow_their[j];
    }

    for (int offset = 16; offset > 0; offset >>= 1) {
        partial += __shfl_down_sync(0xFFFFFFFFu, partial, offset);
    }
    if ((tid & 31) == 0) s_warp_sums[tid >> 5] = partial;
    __syncthreads();

    if (tid == 0) {
        float pred = out_bias[bucket];
        for (int w = 0; w < BLOCK_DIM / 32; ++w) pred += s_warp_sums[w];

        float pred_prob = 1.0f / (1.0f + expf(-pred));
        float eval_prob = 1.0f / (1.0f + expf(-static_cast<float>(rec.score_cp) * inv_eval_scale));
        float game_wdl = 0.5f * static_cast<float>(rec.wdl);
        float target_prob = (1.0f - wdl_weight) * eval_prob + wdl_weight * game_wdl;
        float diff = pred_prob - target_prob;
        atomicAdd(loss_sum_out, diff * diff);
        if (compute_grad) {
            grad_pred_out[pos] = (2.0f / static_cast<float>(batch_size)) * diff * pred_prob * (1.0f - pred_prob);
        }
    }
}

template <int HL, int BLOCK_DIM>
__global__ void backward_pairwise_kernel(
    const V2Record* __restrict__ batch,
    int batch_size,
    const float* __restrict__ out_weight,
    const float* __restrict__ acc_our,
    const float* __restrict__ acc_their,
    const float* __restrict__ grad_pred,
    float* __restrict__ g_ft_bucket,
    float* __restrict__ g_ft_base,
    float* __restrict__ g_ft_bias,
    float* __restrict__ g_out_weight,
    float* __restrict__ g_out_bias
) {
    constexpr int HALF = HL / 2;
    int pos = blockIdx.x;
    if (pos >= batch_size) return;

    const V2Record& rec = batch[pos];
    int tid = threadIdx.x;

    __shared__ std::uint16_t s_our[32];
    __shared__ std::uint16_t s_their[32];
    __shared__ int s_count;
    __shared__ int s_bucket;
    __shared__ float s_dp;

    if (tid < 32) {
        s_our[tid] = rec.our_idx[tid];
        s_their[tid] = rec.their_idx[tid];
    }
    if (tid == 0) {
        s_count = rec.our_count;
        s_bucket = rec.output_bucket;
        s_dp = grad_pred[pos];
        atomicAdd(&g_out_bias[s_bucket], s_dp);
    }
    __syncthreads();

    int count = s_count;
    int bucket = s_bucket;
    float dp = s_dp;
    const float* ow_our = out_weight + (bucket * HL);
    const float* ow_their = ow_our + HALF;
    float* g_ow_our = g_out_weight + (bucket * HL);
    float* g_ow_their = g_ow_our + HALF;

    const float* src_o = acc_our + (static_cast<std::size_t>(pos) * HL);
    const float* src_t = acc_their + (static_cast<std::size_t>(pos) * HL);

    #pragma unroll
    for (int j = tid; j < HALF; j += BLOCK_DIM) {
        int jb = j + HALF;

        // Our perspective pair (j, jb)
        float ao_a = src_o[j], ao_b = src_o[jb];
        if (ao_a > 0.0f && ao_b > 0.0f) {
            float ca = fminf(ao_a, 1.0f);
            float cb = fminf(ao_b, 1.0f);
            atomicAdd(&g_ow_our[j], dp * ca * cb);
            float dw = dp * ow_our[j];
            if (ao_a < 1.0f) {
                float ga = dw * cb;
                atomicAdd(&g_ft_bias[j], ga);
                for (int p = 0; p < count; ++p) {
                    int io = s_our[p];
                    atomicAdd(&g_ft_bucket[io * HL + j], ga);
                    atomicAdd(&g_ft_base[(io % RAW_FEATURES) * HL + j], ga);
                }
            }
            if (ao_b < 1.0f) {
                float gb = dw * ca;
                atomicAdd(&g_ft_bias[jb], gb);
                for (int p = 0; p < count; ++p) {
                    int io = s_our[p];
                    atomicAdd(&g_ft_bucket[io * HL + jb], gb);
                    atomicAdd(&g_ft_base[(io % RAW_FEATURES) * HL + jb], gb);
                }
            }
        }

        // Their perspective pair (j, jb)
        float at_a = src_t[j], at_b = src_t[jb];
        if (at_a > 0.0f && at_b > 0.0f) {
            float ca = fminf(at_a, 1.0f);
            float cb = fminf(at_b, 1.0f);
            atomicAdd(&g_ow_their[j], dp * ca * cb);
            float dw = dp * ow_their[j];
            if (at_a < 1.0f) {
                float ga = dw * cb;
                atomicAdd(&g_ft_bias[j], ga);
                for (int p = 0; p < count; ++p) {
                    int it = s_their[p];
                    atomicAdd(&g_ft_bucket[it * HL + j], ga);
                    atomicAdd(&g_ft_base[(it % RAW_FEATURES) * HL + j], ga);
                }
            }
            if (at_b < 1.0f) {
                float gb = dw * ca;
                atomicAdd(&g_ft_bias[jb], gb);
                for (int p = 0; p < count; ++p) {
                    int it = s_their[p];
                    atomicAdd(&g_ft_bucket[it * HL + jb], gb);
                    atomicAdd(&g_ft_base[(it % RAW_FEATURES) * HL + jb], gb);
                }
            }
        }
    }
}

// ============================================================================
// Fused Adam + EMA Step Kernel
// ============================================================================

__global__ void adam_ema_step_kernel(
    float* __restrict__ param,
    float* __restrict__ ema,
    float* __restrict__ grad,
    float* __restrict__ m,
    float* __restrict__ v,
    int n,
    float lr,
    float inv_bc1,
    float inv_bc2,
    float ema_decay,
    float clamp_abs
) {
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i >= n) return;

    float g = grad[i];
    grad[i] = 0.0f;

    float mi = 0.9f * m[i] + 0.1f * g;
    float vi = 0.999f * v[i] + 0.001f * g * g;
    m[i] = mi;
    v[i] = vi;

    float p = param[i] - lr * (mi * inv_bc1) / (sqrtf(vi * inv_bc2) + 1e-8f);
    if (clamp_abs > 0.0f) {
        p = fminf(fmaxf(p, -clamp_abs), clamp_abs);
    }
    param[i] = p;
    ema[i] = ema_decay * ema[i] + (1.0f - ema_decay) * p;
}

struct ParamBuffer {
    int n = 0;
    float* d_param = nullptr;
    float* d_ema = nullptr;
    float* d_grad = nullptr;
    float* d_m = nullptr;
    float* d_v = nullptr;
    float clamp_abs = 0.0f;

    void allocate(int count, float clamp = 0.0f) {
        n = count;
        clamp_abs = clamp;
        CUDA_CHECK(cudaMalloc(&d_param, sizeof(float) * n));
        CUDA_CHECK(cudaMalloc(&d_ema, sizeof(float) * n));
        CUDA_CHECK(cudaMalloc(&d_grad, sizeof(float) * n));
        CUDA_CHECK(cudaMalloc(&d_m, sizeof(float) * n));
        CUDA_CHECK(cudaMalloc(&d_v, sizeof(float) * n));
        CUDA_CHECK(cudaMemset(d_grad, 0, sizeof(float) * n));
        CUDA_CHECK(cudaMemset(d_m, 0, sizeof(float) * n));
        CUDA_CHECK(cudaMemset(d_v, 0, sizeof(float) * n));
    }

    void upload(const std::vector<float>& host) {
        CUDA_CHECK(cudaMemcpy(d_param, host.data(), sizeof(float) * n, cudaMemcpyHostToDevice));
        CUDA_CHECK(cudaMemcpy(d_ema, host.data(), sizeof(float) * n, cudaMemcpyHostToDevice));
    }

    void download_ema(std::vector<float>& host) const {
        host.resize(n);
        CUDA_CHECK(cudaMemcpy(host.data(), d_ema, sizeof(float) * n, cudaMemcpyDeviceToHost));
    }

    void step(float lr, float inv_bc1, float inv_bc2, float ema_decay) {
        int threads = 256;
        int blocks = (n + threads - 1) / threads;
        adam_ema_step_kernel<<<blocks, threads>>>(d_param, d_ema, d_grad, d_m, d_v, n, lr, inv_bc1, inv_bc2, ema_decay, clamp_abs);
    }

    void free_all() {
        if (d_param) cudaFree(d_param);
        if (d_ema) cudaFree(d_ema);
        if (d_grad) cudaFree(d_grad);
        if (d_m) cudaFree(d_m);
        if (d_v) cudaFree(d_v);
    }
};

template <int HL>
void export_v2_pairwise_nnue(
    const std::string& out_path,
    const ParamBuffer& ft_bucket,
    const ParamBuffer& ft_base,
    const ParamBuffer& ft_bias,
    const ParamBuffer& out_weight,
    const ParamBuffer& out_bias
) {
    std::vector<float> h_ft_bucket, h_ft_base, h_ft_bias, h_out_w, h_out_b;
    ft_bucket.download_ema(h_ft_bucket);
    ft_base.download_ema(h_ft_base);
    ft_bias.download_ema(h_ft_bias);
    out_weight.download_ema(h_out_w);
    out_bias.download_ema(h_out_b);

    std::vector<std::int16_t> q_ft_w(FEATURE_COUNT * HL);
    for (int b = 0; b < KING_BUCKETS; ++b) {
        for (int f = 0; f < RAW_FEATURES; ++f) {
            int dst_row = (b * RAW_FEATURES + f) * HL;
            int base_row = f * HL;
            for (int j = 0; j < HL; ++j) {
                float w = h_ft_bucket[dst_row + j] + h_ft_base[base_row + j];
                int q = static_cast<int>(std::lround(w * V2_QA));
                q_ft_w[dst_row + j] = static_cast<std::int16_t>(std::clamp(q, -32768, 32767));
            }
        }
    }

    std::vector<std::int16_t> q_ft_b(HL);
    for (int j = 0; j < HL; ++j) {
        int q = static_cast<int>(std::lround(h_ft_bias[j] * V2_QA));
        q_ft_b[j] = static_cast<std::int16_t>(std::clamp(q, -32768, 32767));
    }

    std::ofstream f(out_path, std::ios::binary | std::ios::out | std::ios::trunc);
    if (!f) {
        std::cerr << "ERROR: Could not open output path " << out_path << "\n";
        return;
    }

    std::uint16_t hl_u16 = static_cast<std::uint16_t>(HL);
    std::uint16_t ob_u16 = static_cast<std::uint16_t>(OUTPUT_BUCKETS);
    // Low byte: king buckets (16), High byte: activation flag (1 = Pairwise)
    std::uint16_t packed = static_cast<std::uint16_t>((1 << 8) | (KING_BUCKETS & 0xFF));
    f.write(reinterpret_cast<const char*>(&MAGIC_HEADER), 4);
    f.write(reinterpret_cast<const char*>(&FORMAT_VERSION), 4);
    f.write(reinterpret_cast<const char*>(&ARCH_V2), 2);
    f.write(reinterpret_cast<const char*>(&hl_u16), 2);
    f.write(reinterpret_cast<const char*>(&ob_u16), 2);
    f.write(reinterpret_cast<const char*>(&packed), 2);

    f.write(reinterpret_cast<const char*>(q_ft_w.data()), q_ft_w.size() * sizeof(std::int16_t));
    f.write(reinterpret_cast<const char*>(q_ft_b.data()), q_ft_b.size() * sizeof(std::int16_t));

    std::vector<std::int16_t> q_ow(HL);
    for (int ob = 0; ob < OUTPUT_BUCKETS; ++ob) {
        for (int j = 0; j < HL; ++j) {
            int q = static_cast<int>(std::lround(h_out_w[ob * HL + j] * V2_QB));
            q_ow[j] = static_cast<std::int16_t>(std::clamp(q, -127, 127));
        }
        std::int32_t q_ob = static_cast<std::int32_t>(std::lround(h_out_b[ob] * (V2_QA * V2_QB)));
        f.write(reinterpret_cast<const char*>(q_ow.data()), q_ow.size() * sizeof(std::int16_t));
        f.write(reinterpret_cast<const char*>(&q_ob), sizeof(std::int32_t));
    }
    f.close();
    std::cout << "  --> Exported ArchV2-Pairwise (16x768 -> " << HL << "x2 -> 8x1) [EMA] to " << out_path << "\n" << std::flush;
}

template <int HL, int BLOCK_DIM>
int run_training(
    const std::string& data_path,
    const std::string& out_path,
    int epochs,
    int batch_size,
    float lr_start,
    float lr_end,
    float wdl_weight,
    float eval_scale
) {
    std::ifstream in(data_path, std::ios::binary);
    if (!in) {
        std::cerr << "ERROR: Cannot open dataset: " << data_path << "\n";
        return 1;
    }
    V2BinHeader hdr;
    in.read(reinterpret_cast<char*>(&hdr), sizeof(hdr));
    if (std::memcmp(hdr.magic, "ANAKV2BN", 8) != 0 || hdr.rec_size != sizeof(V2Record)) {
        std::cerr << "ERROR: Invalid V2BinHeader in " << data_path << "\n";
        return 1;
    }

    std::uint64_t total_records = hdr.count;
    std::uint64_t val_records = std::min<std::uint64_t>(2000000, total_records / 50);
    std::uint64_t train_records = total_records - val_records;

    cudaDeviceProp prop;
    CUDA_CHECK(cudaGetDeviceProperties(&prop, 0));
    std::cout << "============================================================================\n";
    std::cout << "Anak Native CUDA ArchV2-Pairwise Trainer | (16x768 -> " << HL << ")x2 -> 8x1 | EMA\n";
    std::cout << "GPU: " << prop.name << " | Dataset: " << data_path << " (" << (total_records / 1000000) << "M recs)\n";
    std::cout << "Train: " << train_records << " | Val: " << val_records << " | Epochs: " << epochs
              << " | Batch: " << batch_size << " | LR: " << lr_start << " -> " << lr_end
              << " | WDL: " << wdl_weight << "\n";
    std::cout << "============================================================================\n" << std::flush;

    ParamBuffer ft_bucket, ft_base, ft_bias, out_weight, out_bias;
    ft_bucket.allocate(FEATURE_COUNT * HL, 0.0f);
    ft_base.allocate(RAW_FEATURES * HL, 0.0f);
    ft_bias.allocate(HL, 0.0f);
    out_weight.allocate(OUTPUT_BUCKETS * HL, 1.98f);
    out_bias.allocate(OUTPUT_BUCKETS, 0.0f);

    std::mt19937 rng(42);
    auto init_normal = [&](ParamBuffer& buf, float std_dev) {
        std::normal_distribution<float> dist(0.0f, std_dev);
        std::vector<float> h(buf.n);
        for (int i = 0; i < buf.n; ++i) h[i] = dist(rng);
        buf.upload(h);
    };
    init_normal(ft_bucket, 0.02f);
    init_normal(ft_base, 0.04f);
    ft_bias.upload(std::vector<float>(HL, 0.0f));
    init_normal(out_weight, 1.0f / std::sqrt(static_cast<float>(HL)));
    out_bias.upload(std::vector<float>(OUTPUT_BUCKETS, 0.0f));

    V2Record* d_batch = nullptr;
    float* d_acc_our = nullptr;
    float* d_acc_their = nullptr;
    float* d_grad_pred = nullptr;
    float* d_loss_sum = nullptr;
    CUDA_CHECK(cudaMalloc(&d_batch, sizeof(V2Record) * batch_size));
    CUDA_CHECK(cudaMalloc(&d_acc_our, sizeof(float) * batch_size * HL));
    CUDA_CHECK(cudaMalloc(&d_acc_their, sizeof(float) * batch_size * HL));
    CUDA_CHECK(cudaMalloc(&d_grad_pred, sizeof(float) * batch_size));
    CUDA_CHECK(cudaMalloc(&d_loss_sum, sizeof(float)));

    std::vector<V2Record> val_buf(val_records);
    in.seekg(sizeof(V2BinHeader) + train_records * sizeof(V2Record), std::ios::beg);
    in.read(reinterpret_cast<char*>(val_buf.data()), val_records * sizeof(V2Record));

    constexpr std::uint64_t CHUNK_RECS = 2097152;
    V2Record* h_chunks[2];
    CUDA_CHECK(cudaMallocHost(&h_chunks[0], CHUNK_RECS * sizeof(V2Record)));
    CUDA_CHECK(cudaMallocHost(&h_chunks[1], CHUNK_RECS * sizeof(V2Record)));

    std::uint64_t steps_per_epoch = (train_records + batch_size - 1) / batch_size;
    std::uint64_t total_steps = steps_per_epoch * epochs;
    std::uint64_t warmup_steps = std::min<std::uint64_t>(500, total_steps / 20);
    std::uint64_t global_step = 0;

    float inv_eval_scale = 1.0f / eval_scale;
    float best_val_loss = 1e9f;

    for (int epoch = 1; epoch <= epochs; ++epoch) {
        auto t0 = std::chrono::steady_clock::now();
        std::uint64_t num_chunks = (train_records + CHUNK_RECS - 1) / CHUNK_RECS;
        std::vector<std::uint64_t> chunk_order(num_chunks);
        for (std::uint64_t c = 0; c < num_chunks; ++c) chunk_order[c] = c;
        std::shuffle(chunk_order.begin(), chunk_order.end(), rng);

        auto read_chunk = [&](std::uint64_t chunk_idx, V2Record* dst) -> std::uint64_t {
            std::uint64_t start = chunk_idx * CHUNK_RECS;
            std::uint64_t count = std::min<std::uint64_t>(CHUNK_RECS, train_records - start);
            std::ifstream f_local(data_path, std::ios::binary);
            f_local.seekg(sizeof(V2BinHeader) + start * sizeof(V2Record), std::ios::beg);
            f_local.read(reinterpret_cast<char*>(dst), count * sizeof(V2Record));
            return count;
        };

        std::uint64_t cur_count = read_chunk(chunk_order[0], h_chunks[0]);
        double epoch_loss_sum = 0.0;
        std::uint64_t epoch_seen = 0;

        for (std::uint64_t ci = 0; ci < num_chunks; ++ci) {
            int cur_buf = ci & 1;
            int next_buf = cur_buf ^ 1;
            std::uint64_t next_count = 0;
            std::thread reader_thread;
            if (ci + 1 < num_chunks) {
                reader_thread = std::thread([&]() {
                    next_count = read_chunk(chunk_order[ci + 1], h_chunks[next_buf]);
                });
            }

            V2Record* chunk_ptr = h_chunks[cur_buf];
            for (std::uint64_t off = 0; off < cur_count; off += batch_size) {
                int bs = static_cast<int>(std::min<std::uint64_t>(batch_size, cur_count - off));
                CUDA_CHECK(cudaMemcpy(d_batch, chunk_ptr + off, bs * sizeof(V2Record), cudaMemcpyHostToDevice));
                CUDA_CHECK(cudaMemset(d_loss_sum, 0, sizeof(float)));

                forward_pairwise_kernel<HL, BLOCK_DIM><<<bs, BLOCK_DIM>>>(
                    d_batch, bs,
                    ft_bucket.d_param, ft_base.d_param, ft_bias.d_param,
                    out_weight.d_param, out_bias.d_param,
                    d_acc_our, d_acc_their, d_grad_pred, d_loss_sum,
                    wdl_weight, inv_eval_scale, true
                );

                backward_pairwise_kernel<HL, BLOCK_DIM><<<bs, BLOCK_DIM>>>(
                    d_batch, bs,
                    out_weight.d_param, d_acc_our, d_acc_their, d_grad_pred,
                    ft_bucket.d_grad, ft_base.d_grad, ft_bias.d_grad,
                    out_weight.d_grad, out_bias.d_grad
                );

                global_step++;
                float lr;
                if (global_step <= warmup_steps) {
                    lr = lr_start * (0.1f + 0.9f * (static_cast<float>(global_step) / std::max<std::uint64_t>(1, warmup_steps)));
                } else {
                    double prog = static_cast<double>(global_step - warmup_steps) / std::max<std::uint64_t>(1, total_steps - warmup_steps);
                    prog = std::clamp(prog, 0.0, 1.0);
                    double cos_f = 0.5 * (1.0 + std::cos(3.14159265358979323846 * prog));
                    lr = static_cast<float>(lr_end + (lr_start - lr_end) * cos_f);
                }

                float inv_bc1 = 1.0f / (1.0f - std::pow(0.9f, static_cast<float>(global_step)));
                float inv_bc2 = 1.0f / (1.0f - std::pow(0.999f, static_cast<float>(global_step)));
                float ema_d = std::min(0.999f, (1.0f + global_step) / (10.0f + global_step));

                ft_bucket.step(lr, inv_bc1, inv_bc2, ema_d);
                ft_base.step(lr, inv_bc1, inv_bc2, ema_d);
                ft_bias.step(lr, inv_bc1, inv_bc2, ema_d);
                out_weight.step(lr, inv_bc1, inv_bc2, ema_d);
                out_bias.step(lr, inv_bc1, inv_bc2, ema_d);

                float h_loss = 0.0f;
                CUDA_CHECK(cudaMemcpy(&h_loss, d_loss_sum, sizeof(float), cudaMemcpyDeviceToHost));
                epoch_loss_sum += h_loss;
                epoch_seen += bs;
            }

            if (reader_thread.joinable()) reader_thread.join();
            cur_count = next_count;
        }

        // Validation on EMA shadow weights!
        double val_loss_sum = 0.0;
        for (std::uint64_t off = 0; off < val_records; off += batch_size) {
            int bs = static_cast<int>(std::min<std::uint64_t>(batch_size, val_records - off));
            CUDA_CHECK(cudaMemcpy(d_batch, val_buf.data() + off, bs * sizeof(V2Record), cudaMemcpyHostToDevice));
            CUDA_CHECK(cudaMemset(d_loss_sum, 0, sizeof(float)));
            forward_pairwise_kernel<HL, BLOCK_DIM><<<bs, BLOCK_DIM>>>(
                d_batch, bs,
                ft_bucket.d_ema, ft_base.d_ema, ft_bias.d_ema,
                out_weight.d_ema, out_bias.d_ema,
                d_acc_our, d_acc_their, d_grad_pred, d_loss_sum,
                wdl_weight, inv_eval_scale, false
            );
            float h_vloss = 0.0f;
            CUDA_CHECK(cudaMemcpy(&h_vloss, d_loss_sum, sizeof(float), cudaMemcpyDeviceToHost));
            val_loss_sum += h_vloss;
        }

        double dt = std::max(0.001, std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count());
        float avg_train = static_cast<float>(epoch_loss_sum / epoch_seen);
        float avg_val = static_cast<float>(val_loss_sum / val_records);

        std::cout << "Epoch " << std::setw(2) << epoch << "/" << epochs
                  << " | Train WDL MSE: " << std::fixed << std::setprecision(6) << avg_train
                  << " | EMA Val WDL MSE: " << avg_val
                  << " | " << std::setprecision(2) << (double(epoch_seen) / dt / 1e6) << "M pos/s ("
                  << std::setprecision(1) << dt << "s)\n" << std::flush;

        if (avg_val < best_val_loss) {
            best_val_loss = avg_val;
            export_v2_pairwise_nnue<HL>(out_path, ft_bucket, ft_base, ft_bias, out_weight, out_bias);
        }
    }

    CUDA_CHECK(cudaFreeHost(h_chunks[0]));
    CUDA_CHECK(cudaFreeHost(h_chunks[1]));
    cudaFree(d_batch);
    cudaFree(d_acc_our);
    cudaFree(d_acc_their);
    cudaFree(d_grad_pred);
    cudaFree(d_loss_sum);
    ft_bucket.free_all();
    ft_base.free_all();
    ft_bias.free_all();
    out_weight.free_all();
    out_bias.free_all();

    std::cout << "Training complete! Best EMA Val WDL MSE: " << std::fixed << std::setprecision(6) << best_val_loss << "\n";
    return 0;
}

int main(int argc, char** argv) {
    std::string data_path = "training/data/v2_train_300m.v2bin";
    std::string out_path = "networks/exp_v2/anak.nnue";
    int hidden_dim = 1024;
    int epochs = 6;
    int batch_size = 16384;
    float lr_start = 1.0e-3f;
    float lr_end = 1.0e-5f;
    float wdl_weight = 0.25f;
    float eval_scale = 400.0f;

    for (int i = 1; i < argc; ++i) {
        std::string a = argv[i];
        if (a == "--data" && i + 1 < argc) data_path = argv[++i];
        else if (a == "--output" && i + 1 < argc) out_path = argv[++i];
        else if (a == "--hidden-dim" && i + 1 < argc) hidden_dim = std::atoi(argv[++i]);
        else if (a == "--epochs" && i + 1 < argc) epochs = std::atoi(argv[++i]);
        else if (a == "--batch-size" && i + 1 < argc) batch_size = std::atoi(argv[++i]);
        else if (a == "--lr" && i + 1 < argc) lr_start = std::atof(argv[++i]);
        else if (a == "--lr-min" && i + 1 < argc) lr_end = std::atof(argv[++i]);
        else if (a == "--wdl" && i + 1 < argc) wdl_weight = std::atof(argv[++i]);
        else if (a == "--scale" && i + 1 < argc) eval_scale = std::atof(argv[++i]);
    }

    if (hidden_dim == 1536) {
        return run_training<1536, 256>(data_path, out_path, epochs, batch_size, lr_start, lr_end, wdl_weight, eval_scale);
    }
    return run_training<1024, 256>(data_path, out_path, epochs, batch_size, lr_start, lr_end, wdl_weight, eval_scale);
}
