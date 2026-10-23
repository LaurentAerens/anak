/*
 * High-Speed Multi-Threaded Stockfish BINP (.binpack) -> Anak NNUE (.binpack) Converter
 * Decompresses Stockfish Depth-9 (500M_d9_*.binpack) BINP chunks in parallel across all
 * CPU threads, shuffles records in bounded-memory buffers, and writes all 8 expert packs:
 *   - open.binpack, closed.binpack, endgame.binpack, queenless.binpack,
 *     imbalance.binpack, attack.binpack, micro.binpack, volatility.binpack
 */

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <mutex>
#include <random>
#include <string>
#include <thread>
#include <vector>

#include "binpack.h"

#pragma pack(push, 1)
struct AnakBinpackHeader {
    char magic[8];          // "ANAKBINP"
    std::uint32_t version;  // 1
    std::uint32_t rec_size; // 144
    std::uint64_t count;    // total records
    char model_tag[16];     // "open", "closed", "micro", etc.
    char reserved[24];      // zero-filled
};

struct AnakBinpackRecord {
    std::uint16_t our_indices[32];
    std::uint16_t their_indices[32];
    float target_eval;
    float target_wdl;
    float openness;
    std::uint8_t piece_count;
    std::uint8_t flags;
    std::uint16_t reserved;
};
#pragma pack(pop)

static_assert(sizeof(AnakBinpackHeader) == 64, "Header must be 64 bytes");
static_assert(sizeof(AnakBinpackRecord) == 144, "Record must be 144 bytes");

class ThreadSafeBufferedBinpackWriter {
public:
    static constexpr std::size_t CHUNK_CAPACITY = 500000;

    ThreadSafeBufferedBinpackWriter(const std::string& path, const char* tag, std::uint64_t max_records, std::uint64_t seed)
        : out_(path, std::ios::binary | std::ios::out | std::ios::trunc),
          max_records_(max_records),
          total_written_(0),
          current_size_(0),
          rng_(seed),
          closed_(false) {
        std::memset(&header_, 0, sizeof(header_));
        std::memcpy(header_.magic, "ANAKBINP", 8);
        header_.version = 1;
        header_.rec_size = 144;
        header_.count = 0;
        std::strncpy(header_.model_tag, tag, 15);

        out_.write(reinterpret_cast<const char*>(&header_), sizeof(header_));
        buffer_.reserve(std::min<std::uint64_t>(max_records_, CHUNK_CAPACITY));
    }

    ~ThreadSafeBufferedBinpackWriter() {
        close();
    }

    bool is_full() const {
        return current_size_.load(std::memory_order_relaxed) >= max_records_;
    }

    std::uint64_t size() const {
        return current_size_.load(std::memory_order_relaxed);
    }

    void append_batch(std::vector<AnakBinpackRecord>& batch) {
        if (batch.empty()) return;
        if (is_full()) {
            batch.clear();
            return;
        }

        std::lock_guard<std::mutex> lock(mtx_);
        for (const auto& rec : batch) {
            if (total_written_ + buffer_.size() >= max_records_) {
                break;
            }
            buffer_.push_back(rec);
            if (buffer_.size() >= CHUNK_CAPACITY) {
                flush_unlocked();
            }
        }
        current_size_.store(total_written_ + buffer_.size(), std::memory_order_relaxed);
        batch.clear();
    }

    void close() {
        std::lock_guard<std::mutex> lock(mtx_);
        if (closed_) return;
        flush_unlocked();
        header_.count = total_written_;
        out_.seekp(0, std::ios::beg);
        out_.write(reinterpret_cast<const char*>(&header_), sizeof(header_));
        out_.close();
        current_size_.store(total_written_, std::memory_order_relaxed);
        closed_ = true;
    }

private:
    void flush_unlocked() {
        if (buffer_.empty()) return;
        std::shuffle(buffer_.begin(), buffer_.end(), rng_);
        out_.write(reinterpret_cast<const char*>(buffer_.data()), buffer_.size() * sizeof(AnakBinpackRecord));
        total_written_ += buffer_.size();
        buffer_.clear();
    }

    mutable std::mutex mtx_;
    std::ofstream out_;
    AnakBinpackHeader header_;
    std::vector<AnakBinpackRecord> buffer_;
    std::uint64_t max_records_;
    std::uint64_t total_written_;
    std::atomic<std::uint64_t> current_size_;
    std::mt19937_64 rng_;
    bool closed_;
};

static inline float compute_openness(const chess::Position& pos) {
    const std::uint64_t wp = pos.piecesBB(chess::Piece(chess::PieceType::Pawn, chess::Color::White)).bits();
    const std::uint64_t bp = pos.piecesBB(chess::Piece(chess::PieceType::Pawn, chess::Color::Black)).bits();
    const std::uint64_t all_pawns = wp | bp;
    int total_pawns = std::popcount(all_pawns);

    std::uint64_t locked_rams = wp & (bp >> 8);
    int locked_count = std::popcount(locked_rams);

    int open_files = 0;
    int semi_open_files = 0;
    for (int f = 0; f < 8; ++f) {
        std::uint64_t mask = 0x0101010101010101ULL << f;
        bool has_w = (wp & mask) != 0ULL;
        bool has_b = (bp & mask) != 0ULL;
        if (!has_w && !has_b) open_files++;
        else if (!has_w || !has_b) semi_open_files++;
    }

    constexpr std::uint64_t file_a = 0x0101010101010101ULL;
    constexpr std::uint64_t file_h = 0x0101010101010101ULL << 7;
    std::uint64_t white_attacks = ((wp << 7) & ~file_h) | ((wp << 9) & ~file_a);
    int tension_count = std::popcount(white_attacks & bp);

    constexpr std::uint64_t center_mask = (0x0101010101010101ULL << 2) | (0x0101010101010101ULL << 3) |
                                          (0x0101010101010101ULL << 4) | (0x0101010101010101ULL << 5);
    int center_pawns = std::popcount(all_pawns & center_mask);

    float openness = 0.35f
        + (open_files * 0.12f)
        + (semi_open_files * 0.05f)
        - (locked_count * 0.10f)
        + (tension_count * 0.04f)
        + ((8 - center_pawns) * 0.03f)
        + ((16 - total_pawns) * 0.02f);

    return std::clamp(openness, 0.0f, 1.0f);
}

class MultiFileChunkDispenser {
public:
    explicit MultiFileChunkDispenser(const std::vector<std::string>& paths)
        : paths_(paths), file_idx_(0) {
        open_next_file();
    }

    bool next_chunk(std::vector<unsigned char>& chunk_out) {
        std::lock_guard<std::mutex> lock(mtx_);
        while (true) {
            if (current_file_ && current_file_->hasNextChunk()) {
                current_file_->readNextChunkInto(chunk_out);
                if (!chunk_out.empty()) {
                    chunks_dispensed_++;
                    return true;
                }
            }
            if (!open_next_file()) {
                return false;
            }
        }
    }

    std::uint64_t chunks_dispensed() const {
        return chunks_dispensed_.load(std::memory_order_relaxed);
    }

private:
    bool open_next_file() {
        while (file_idx_ < paths_.size()) {
            std::string p = paths_[file_idx_++];
            current_file_ = std::make_unique<binpack::CompressedTrainingDataFile>(p, std::ios_base::in);
            if (current_file_->hasNextChunk()) {
                return true;
            }
        }
        current_file_.reset();
        return false;
    }

    std::mutex mtx_;
    std::vector<std::string> paths_;
    std::size_t file_idx_;
    std::unique_ptr<binpack::CompressedTrainingDataFile> current_file_;
    std::atomic<std::uint64_t> chunks_dispensed_{0};
};

int main(int argc, char** argv) {
    if (argc < 4) {
        std::cerr << "Usage: sf_binpack_to_anak <output_dir> <max_per_pack> <input1.binpack> [input2.binpack ...]\n";
        return 1;
    }

    std::string out_dir = argv[1];
    std::uint64_t max_per_pack = std::stoull(argv[2]);

    std::vector<std::string> in_paths;
    for (int arg_i = 3; arg_i < argc; ++arg_i) {
        in_paths.emplace_back(argv[arg_i]);
    }

    ThreadSafeBufferedBinpackWriter open_recs(out_dir + "/open.binpack", "open", max_per_pack, 42);
    ThreadSafeBufferedBinpackWriter closed_recs(out_dir + "/closed.binpack", "closed", max_per_pack, 43);
    ThreadSafeBufferedBinpackWriter endgame_recs(out_dir + "/endgame.binpack", "endgame", max_per_pack, 44);
    ThreadSafeBufferedBinpackWriter queenless_recs(out_dir + "/queenless.binpack", "queenless", max_per_pack, 45);
    ThreadSafeBufferedBinpackWriter imbalance_recs(out_dir + "/imbalance.binpack", "imbalance", max_per_pack, 46);
    ThreadSafeBufferedBinpackWriter attack_recs(out_dir + "/attack.binpack", "attack", max_per_pack, 47);
    ThreadSafeBufferedBinpackWriter micro_recs(out_dir + "/micro.binpack", "micro", max_per_pack, 48);
    ThreadSafeBufferedBinpackWriter volatility_recs(out_dir + "/volatility.binpack", "volatility", max_per_pack, 49);

    MultiFileChunkDispenser dispenser(in_paths);
    std::atomic<std::uint64_t> total_read{0};
    std::atomic<bool> stop_flag{false};

    auto all_full = [&]() {
        return open_recs.is_full() &&
               closed_recs.is_full() &&
               endgame_recs.is_full() &&
               queenless_recs.is_full() &&
               imbalance_recs.is_full() &&
               attack_recs.is_full() &&
               micro_recs.is_full() &&
               volatility_recs.is_full();
    };

    unsigned int num_threads = std::max(1u, std::thread::hardware_concurrency());
    std::cout << "  [C++20 Multi-Threaded Unpacker] Using " << num_threads << " hardware threads across "
              << in_paths.size() << " archive(s)...\n" << std::flush;

    auto start_time = std::chrono::steady_clock::now();
    std::mutex print_mtx;
    std::uint64_t last_printed_million = 0;

    auto worker_fn = [&](unsigned int tid) {
        std::vector<unsigned char> chunk;
        chunk.reserve(1024 * 1024);

        constexpr std::size_t LOCAL_BATCH_SIZE = 8192;
        std::vector<AnakBinpackRecord> b_open, b_closed, b_endgame, b_queenless, b_imbalance, b_attack, b_micro, b_vol;
        b_open.reserve(LOCAL_BATCH_SIZE);
        b_closed.reserve(LOCAL_BATCH_SIZE);
        b_endgame.reserve(LOCAL_BATCH_SIZE);
        b_queenless.reserve(LOCAL_BATCH_SIZE);
        b_imbalance.reserve(LOCAL_BATCH_SIZE);
        b_attack.reserve(LOCAL_BATCH_SIZE);
        b_micro.reserve(LOCAL_BATCH_SIZE);
        b_vol.reserve(LOCAL_BATCH_SIZE);

        auto flush_local = [&]() {
            open_recs.append_batch(b_open);
            closed_recs.append_batch(b_closed);
            endgame_recs.append_batch(b_endgame);
            queenless_recs.append_batch(b_queenless);
            imbalance_recs.append_batch(b_imbalance);
            attack_recs.append_batch(b_attack);
            micro_recs.append_batch(b_micro);
            volatility_recs.append_batch(b_vol);
        };

        while (!stop_flag.load(std::memory_order_relaxed)) {
            if (all_full()) {
                stop_flag.store(true, std::memory_order_relaxed);
                break;
            }

            if (!dispenser.next_chunk(chunk)) {
                break;
            }

            binpack::ChunkReader chunk_reader;
            float prev_ev_pawns = 0.0f;
            std::uint64_t local_read = 0;

            while (chunk_reader.hasNext(chunk)) {
                auto e = chunk_reader.next(chunk);
                local_read++;

                if (std::abs(int(e.score)) > 3500) {
                    continue;
                }

                bool in_check = e.isInCheck();
                if (in_check) {
                    continue;
                }
                bool is_capture = e.isCapturingMove();

                float ev_pawns = std::clamp(float(e.score) / 270.0f, -15.0f, 15.0f);
                float eval_delta = std::abs(ev_pawns + prev_ev_pawns);
                prev_ev_pawns = ev_pawns;

                float game_wdl = (e.result > 0) ? 1.0f : ((e.result < 0) ? 0.0f : 0.5f);
                float eval_wdl = 1.0f / (1.0f + std::exp(-ev_pawns / 2.5f));
                float wdl = 0.75f * eval_wdl + 0.25f * game_wdl;

                float openness = compute_openness(e.pos);
                bool is_blunder = std::abs(ev_pawns) >= 2.0f;

                AnakBinpackRecord rec;
                for (int i = 0; i < 32; ++i) {
                    rec.our_indices[i] = 0xFFFF;
                    rec.their_indices[i] = 0xFFFF;
                }

                bool white_turn = (e.pos.sideToMove() == chess::Color::White);
                int idx = 0;

                int w_pawns = 0, b_pawns = 0;
                int w_knights = 0, b_knights = 0;
                int w_bishops = 0, b_bishops = 0;
                int w_rooks = 0, b_rooks = 0;
                int w_queens = 0, b_queens = 0;
                int w_king_file = 4, b_king_file = 4;

                std::uint64_t occ = (e.pos.piecesBB(chess::Color::White) | e.pos.piecesBB(chess::Color::Black)).bits();
                while (occ && idx < 32) {
                    int sq = std::countr_zero(occ);
                    occ &= (occ - 1);

                    chess::Piece p = e.pos.pieceAt(chess::Square(sq));
                    int p_idx = static_cast<int>(p.type());
                    int c_idx = (p.color() == chess::Color::White) ? 0 : 1;

                    if (c_idx == 0) {
                        if (p_idx == 0) w_pawns++;
                        else if (p_idx == 1) w_knights++;
                        else if (p_idx == 2) w_bishops++;
                        else if (p_idx == 3) w_rooks++;
                        else if (p_idx == 4) w_queens++;
                        else if (p_idx == 5) w_king_file = sq & 7;
                    } else {
                        if (p_idx == 0) b_pawns++;
                        else if (p_idx == 1) b_knights++;
                        else if (p_idx == 2) b_bishops++;
                        else if (p_idx == 3) b_rooks++;
                        else if (p_idx == 4) b_queens++;
                        else if (p_idx == 5) b_king_file = sq & 7;
                    }

                    std::uint16_t white_feat = static_cast<std::uint16_t>(((c_idx * 6) + p_idx) * 64 + sq);
                    std::uint16_t black_feat = static_cast<std::uint16_t>((((1 - c_idx) * 6) + p_idx) * 64 + (sq ^ 56));

                    if (white_turn) {
                        rec.our_indices[idx] = white_feat;
                        rec.their_indices[idx] = black_feat;
                    } else {
                        rec.our_indices[idx] = black_feat;
                        rec.their_indices[idx] = white_feat;
                    }
                    idx++;
                }

                int total_pieces = idx;
                int total_queens = w_queens + b_queens;
                bool has_queens = total_queens > 0;
                int w_minors = w_bishops + w_knights;
                int b_minors = b_bishops + b_knights;
                int w_mat = w_queens * 9 + w_rooks * 5 + w_bishops * 3 + w_knights * 3 + w_pawns;
                int b_mat = b_queens * 9 + b_rooks * 5 + b_bishops * 3 + b_knights * 3 + b_pawns;
                int material_imbalance = std::abs(w_mat - b_mat);

                bool is_endgame = total_pieces <= 14;
                bool is_queenless = (total_queens == 0);
                bool is_imbalance = (w_queens != b_queens) ||
                                    (w_rooks != b_rooks && w_minors != b_minors) ||
                                    (material_imbalance >= 2);
                bool opposite_castling = has_queens &&
                                         ((w_king_file <= 2 && b_king_file >= 5) ||
                                          (w_king_file >= 5 && b_king_file <= 2));

                int expert_id = 0;
                if (is_endgame) {
                    expert_id = 2;
                } else if (is_queenless) {
                    expert_id = 3;
                } else if (is_imbalance) {
                    expert_id = 4;
                } else if (opposite_castling) {
                    expert_id = 5;
                } else if (openness >= 0.50f) {
                    expert_id = 0;
                } else {
                    expert_id = 1;
                }

                rec.target_eval = ev_pawns;
                rec.target_wdl = wdl;
                rec.openness = openness;
                rec.piece_count = static_cast<std::uint8_t>(idx);
                rec.flags = (is_blunder ? 1 : 0) | (has_queens ? 2 : 0) | (in_check ? 4 : 0);
                rec.reserved = 0;

                bool cross_sample_20pct = (local_read % 5 == 0); // 20% cross-domain sampling for 80/20 expert mixture
                bool cross_sample_10pct = (local_read % 10 == 0); // 10% cross-domain sampling for volatility mixture

                if (!is_capture) {
                    if ((expert_id == 0 || openness >= 0.45f || cross_sample_20pct) && !open_recs.is_full()) {
                        b_open.push_back(rec);
                        if (b_open.size() >= LOCAL_BATCH_SIZE) open_recs.append_batch(b_open);
                    }
                    if ((expert_id == 1 || openness < 0.52f || cross_sample_20pct) && !closed_recs.is_full()) {
                        b_closed.push_back(rec);
                        if (b_closed.size() >= LOCAL_BATCH_SIZE) closed_recs.append_batch(b_closed);
                    }
                    if ((expert_id == 2 || is_endgame || cross_sample_20pct) && !endgame_recs.is_full()) {
                        b_endgame.push_back(rec);
                        if (b_endgame.size() >= LOCAL_BATCH_SIZE) endgame_recs.append_batch(b_endgame);
                    }
                    if ((expert_id == 3 || is_queenless || cross_sample_20pct) && !queenless_recs.is_full()) {
                        b_queenless.push_back(rec);
                        if (b_queenless.size() >= LOCAL_BATCH_SIZE) queenless_recs.append_batch(b_queenless);
                    }
                    if ((expert_id == 4 || is_imbalance || cross_sample_20pct) && !imbalance_recs.is_full()) {
                        b_imbalance.push_back(rec);
                        if (b_imbalance.size() >= LOCAL_BATCH_SIZE) imbalance_recs.append_batch(b_imbalance);
                    }
                    if ((expert_id == 5 || opposite_castling || cross_sample_20pct) && !attack_recs.is_full()) {
                        b_attack.push_back(rec);
                        if (b_attack.size() >= LOCAL_BATCH_SIZE) attack_recs.append_batch(b_attack);
                    }
                    if (!micro_recs.is_full() && (is_blunder || (local_read % 2 == 0))) {
                        b_micro.push_back(rec);
                        if (b_micro.size() >= LOCAL_BATCH_SIZE) micro_recs.append_batch(b_micro);
                    }
                }

                if (!volatility_recs.is_full() &&
                    (is_capture || is_blunder || opposite_castling || is_imbalance || eval_delta >= 0.60f || cross_sample_10pct)) {
                    AnakBinpackRecord vol_rec = rec;
                    float sign = (ev_pawns >= 0.0f) ? 1.0f : -1.0f;
                    float vol_mag = std::min(10.0f, (eval_delta * 1.25f) + (is_capture ? 1.0f : 0.0f) + (std::abs(ev_pawns) * 0.35f));
                    vol_rec.target_eval = sign * vol_mag;
                    b_vol.push_back(vol_rec);
                    if (b_vol.size() >= LOCAL_BATCH_SIZE) volatility_recs.append_batch(b_vol);
                }
            }

            flush_local();
            std::uint64_t new_total = total_read.fetch_add(local_read, std::memory_order_relaxed) + local_read;
            std::uint64_t cur_million = new_total / 2000000;

            if (cur_million > last_printed_million) {
                std::lock_guard<std::mutex> plock(print_mtx);
                if (cur_million > last_printed_million) {
                    last_printed_million = cur_million;
                    auto now = std::chrono::steady_clock::now();
                    double elapsed = std::max(0.001, std::chrono::duration<double>(now - start_time).count());
                    double mps = (double(new_total) / elapsed) / 1e6;
                    std::cout << "\r  [Unpacking] " << (new_total / 1000000) << "M read ("
                              << std::fixed << std::setprecision(1) << mps << "M pos/s) | "
                              << "open:" << (open_recs.size() / 1000) << "k "
                              << "closed:" << (closed_recs.size() / 1000) << "k "
                              << "end:" << (endgame_recs.size() / 1000) << "k "
                              << "qless:" << (queenless_recs.size() / 1000) << "k "
                              << "imb:" << (imbalance_recs.size() / 1000) << "k "
                              << "atk:" << (attack_recs.size() / 1000) << "k "
                              << "mic:" << (micro_recs.size() / 1000) << "k "
                              << "vol:" << (volatility_recs.size() / 1000) << "k   " << std::flush;
                }
            }
        }

        flush_local();
    };

    std::vector<std::thread> threads;
    threads.reserve(num_threads);
    for (unsigned int t = 0; t < num_threads; ++t) {
        threads.emplace_back(worker_fn, t);
    }
    for (auto& th : threads) {
        th.join();
    }

    std::cout << "\n  [Finalizing & flushing remaining buffers to disk...]\n" << std::flush;

    open_recs.close();
    closed_recs.close();
    endgame_recs.close();
    queenless_recs.close();
    imbalance_recs.close();
    attack_recs.close();
    micro_recs.close();
    volatility_recs.close();

    std::cout << "Converted & shuffled " << total_read.load() << " Stockfish d9 positions -> "
              << "open: " << open_recs.size() << ", "
              << "closed: " << closed_recs.size() << ", "
              << "endgame: " << endgame_recs.size() << ", "
              << "queenless: " << queenless_recs.size() << ", "
              << "imbalance: " << imbalance_recs.size() << ", "
              << "attack: " << attack_recs.size() << ", "
              << "micro: " << micro_recs.size() << ", "
              << "volatility: " << volatility_recs.size() << "\n";
    return 0;
}
