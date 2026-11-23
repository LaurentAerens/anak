/*
 * High-Speed Multi-Threaded Stockfish BINP (.binpack) -> Anak ArchV2 (.v2bin) Converter
 *
 * Produces a single clean, natural-distribution, shuffled binary dataset with:
 *   - Pre-computed TRUE horizontal mirroring (sq ^ 7 when perspective king is on files E-H)
 *   - Pre-computed 16 king-bucket indices [0..12287] (sentinel 0xFFFF)
 *   - Raw Stockfish internal score (int16) and RAW game WDL (0=Loss, 1=Draw, 2=Win, NO pre-blending!)
 *   - Proper filtering: ply >= 16, !inCheck, !capture, !enPassant, !promotion, |score| <= 3000
 *   - Output bucket pre-computed: clamp((pieceCount - 2) / 4, 0, 7)
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
struct V2BinHeader {
    char magic[8];          // "ANAKV2BN"
    std::uint32_t version;  // 1
    std::uint32_t rec_size; // 136
    std::uint64_t count;    // total records
    char reserved[40];
};

struct V2Record {
    std::uint16_t our_idx[32];    // Pre-mirrored & king-bucketed [0..12287], 0xFFFF sentinel
    std::uint16_t their_idx[32];  // Pre-mirrored & king-bucketed [0..12287], 0xFFFF sentinel
    std::int16_t score_cp;        // Raw Stockfish internal score clamped [-3000, 3000]
    std::uint8_t wdl;             // Raw game result: 0 = Loss, 1 = Draw, 2 = Win
    std::uint8_t output_bucket;   // 0..7 = clamp((piece_count - 2) / 4, 0, 7)
    std::uint8_t piece_count;     // 2..32
    std::uint8_t our_count;       // Number of active features in our_idx
    std::uint8_t their_count;     // Number of active features in their_idx
    std::uint8_t ply;             // Clamped ply [0..255]
};
#pragma pack(pop)

static_assert(sizeof(V2BinHeader) == 64, "V2BinHeader must be 64 bytes");
static_assert(sizeof(V2Record) == 136, "V2Record must be 136 bytes");

static inline int get_king_bucket(int sq) {
    int f = sq & 7;
    int r = sq >> 3;
    int folded = (f >= 4) ? (7 - f) : f;
    return ((r >> 1) << 2) | folded;
}

class ShuffledV2Writer {
public:
    static constexpr std::size_t CHUNK_CAPACITY = 1000000;

    ShuffledV2Writer(const std::string& path, std::uint64_t max_records, std::uint64_t seed)
        : out_(path, std::ios::binary | std::ios::out | std::ios::trunc),
          max_records_(max_records),
          total_written_(0),
          current_size_(0),
          rng_(seed),
          closed_(false) {
        std::memset(&header_, 0, sizeof(header_));
        std::memcpy(header_.magic, "ANAKV2BN", 8);
        header_.version = 1;
        header_.rec_size = sizeof(V2Record);
        header_.count = 0;
        out_.write(reinterpret_cast<const char*>(&header_), sizeof(header_));
        buffer_.reserve(std::min<std::uint64_t>(max_records_, CHUNK_CAPACITY));
    }

    ~ShuffledV2Writer() { close(); }

    bool is_full() const {
        return current_size_.load(std::memory_order_relaxed) >= max_records_;
    }

    std::uint64_t size() const {
        return current_size_.load(std::memory_order_relaxed);
    }

    void append_batch(std::vector<V2Record>& batch) {
        if (batch.empty()) return;
        if (is_full()) {
            batch.clear();
            return;
        }
        std::lock_guard<std::mutex> lock(mtx_);
        for (const auto& rec : batch) {
            if (total_written_ + buffer_.size() >= max_records_) break;
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
        out_.write(reinterpret_cast<const char*>(buffer_.data()), buffer_.size() * sizeof(V2Record));
        total_written_ += buffer_.size();
        buffer_.clear();
    }

    mutable std::mutex mtx_;
    std::ofstream out_;
    V2BinHeader header_;
    std::vector<V2Record> buffer_;
    std::uint64_t max_records_;
    std::uint64_t total_written_;
    std::atomic<std::uint64_t> current_size_;
    std::mt19937_64 rng_;
    bool closed_;
};

class MultiFileChunkDispenser {
public:
    explicit MultiFileChunkDispenser(const std::vector<std::string>& paths)
        : next_idx_(0) {
        files_.reserve(paths.size());
        for (const auto& p : paths) {
            auto f = std::make_unique<binpack::CompressedTrainingDataFile>(p, std::ios_base::in);
            if (f->hasNextChunk()) {
                files_.push_back(std::move(f));
            }
        }
    }

    bool next_chunk(std::vector<unsigned char>& chunk_out) {
        std::lock_guard<std::mutex> lock(mtx_);
        while (!files_.empty()) {
            std::size_t idx = next_idx_ % files_.size();
            next_idx_++;
            if (files_[idx]->hasNextChunk()) {
                files_[idx]->readNextChunkInto(chunk_out);
                if (!chunk_out.empty()) return true;
            }
            files_.erase(files_.begin() + idx);
        }
        return false;
    }

private:
    std::mutex mtx_;
    std::vector<std::unique_ptr<binpack::CompressedTrainingDataFile>> files_;
    std::size_t next_idx_;
};

int main(int argc, char** argv) {
    if (argc < 4) {
        std::cerr << "Usage: sf_binpack_to_v2 <output.v2bin> <max_records> <input1.binpack> [input2.binpack ...]\n";
        return 1;
    }

    std::string out_path = argv[1];
    std::uint64_t max_records = std::stoull(argv[2]);
    std::vector<std::string> in_paths;
    for (int i = 3; i < argc; ++i) in_paths.emplace_back(argv[i]);

    ShuffledV2Writer writer(out_path, max_records, 20261123ULL);
    MultiFileChunkDispenser dispenser(in_paths);
    std::atomic<std::uint64_t> total_read{0};
    std::atomic<bool> stop_flag{false};

    unsigned int num_threads = std::max(1u, std::thread::hardware_concurrency());
    std::cout << "[sf_binpack_to_v2] Unpacking up to " << max_records << " clean positions with "
              << num_threads << " threads from " << in_paths.size() << " archive(s)...\n" << std::flush;

    auto start_time = std::chrono::steady_clock::now();
    std::mutex print_mtx;
    std::uint64_t last_printed = 0;

    auto worker = [&](unsigned int tid) {
        std::vector<unsigned char> chunk;
        chunk.reserve(1024 * 1024);
        std::vector<V2Record> local_batch;
        local_batch.reserve(8192);

        while (!stop_flag.load(std::memory_order_relaxed)) {
            if (writer.is_full()) {
                stop_flag.store(true, std::memory_order_relaxed);
                break;
            }
            if (!dispenser.next_chunk(chunk)) break;

            binpack::ChunkReader reader;
            std::uint64_t local_read = 0;

            while (reader.hasNext(chunk)) {
                auto e = reader.next(chunk);
                local_read++;

                // Clean quiet position filtering:
                if (e.ply < 16) continue;
                if (std::abs(int(e.score)) > 3000) continue;
                if (e.isInCheck()) continue;
                if (e.isCapturingMove()) continue;
                if (e.move.type == chess::MoveType::EnPassant || e.move.type == chess::MoveType::Promotion) continue;

                // Find kings first for true horizontal mirroring
                std::uint64_t wk_bb = e.pos.piecesBB(chess::Piece(chess::PieceType::King, chess::Color::White)).bits();
                std::uint64_t bk_bb = e.pos.piecesBB(chess::Piece(chess::PieceType::King, chess::Color::Black)).bits();
                if (!wk_bb || !bk_bb) continue;

                int w_king_sq = std::countr_zero(wk_bb);
                int b_king_sq = std::countr_zero(bk_bb);
                int w_bucket = get_king_bucket(w_king_sq);
                int b_bucket = get_king_bucket(b_king_sq ^ 56);
                int w_flip = (w_king_sq & 4) ? 7 : 0;
                int b_flip = (b_king_sq & 4) ? 7 : 0;
                int w_offset = w_bucket * 768;
                int b_offset = b_bucket * 768;

                V2Record rec;
                std::fill(std::begin(rec.our_idx), std::end(rec.our_idx), std::uint16_t(0xFFFF));
                std::fill(std::begin(rec.their_idx), std::end(rec.their_idx), std::uint16_t(0xFFFF));

                bool white_turn = (e.pos.sideToMove() == chess::Color::White);
                std::uint64_t occ = (e.pos.piecesBB(chess::Color::White) | e.pos.piecesBB(chess::Color::Black)).bits();
                int idx = 0;
                while (occ && idx < 32) {
                    int sq = std::countr_zero(occ);
                    occ &= (occ - 1);
                    chess::Piece p = e.pos.pieceAt(chess::Square(sq));
                    int p_idx = static_cast<int>(p.type());
                    int c_idx = (p.color() == chess::Color::White) ? 0 : 1;

                    std::uint16_t w_feat = static_cast<std::uint16_t>(w_offset + (((c_idx * 6) + p_idx) << 6) + (sq ^ w_flip));
                    std::uint16_t b_feat = static_cast<std::uint16_t>(b_offset + ((((1 - c_idx) * 6) + p_idx) << 6) + ((sq ^ 56) ^ b_flip));

                    if (white_turn) {
                        rec.our_idx[idx] = w_feat;
                        rec.their_idx[idx] = b_feat;
                    } else {
                        rec.our_idx[idx] = b_feat;
                        rec.their_idx[idx] = w_feat;
                    }
                    idx++;
                }

                if (idx < 2) continue;

                rec.score_cp = static_cast<std::int16_t>(std::clamp(int(e.score), -3000, 3000));
                rec.wdl = (e.result > 0) ? 2 : ((e.result < 0) ? 0 : 1);
                rec.piece_count = static_cast<std::uint8_t>(idx);
                rec.output_bucket = static_cast<std::uint8_t>(std::clamp((idx - 2) / 4, 0, 7));
                rec.our_count = static_cast<std::uint8_t>(idx);
                rec.their_count = static_cast<std::uint8_t>(idx);
                rec.ply = static_cast<std::uint8_t>(std::min<int>(255, e.ply));

                local_batch.push_back(rec);
                if (local_batch.size() >= 8192) {
                    writer.append_batch(local_batch);
                    if (writer.is_full()) {
                        stop_flag.store(true, std::memory_order_relaxed);
                        break;
                    }
                }
            }

            writer.append_batch(local_batch);
            std::uint64_t n = total_read.fetch_add(local_read, std::memory_order_relaxed) + local_read;
            std::uint64_t mil = n / 10000000;
            if (mil > last_printed) {
                std::lock_guard<std::mutex> plock(print_mtx);
                if (mil > last_printed) {
                    last_printed = mil;
                    double dt = std::max(0.001, std::chrono::duration<double>(std::chrono::steady_clock::now() - start_time).count());
                    std::cout << "\r  Read " << (n / 1000000) << "M (" << std::fixed << std::setprecision(1)
                              << (double(n) / dt / 1e6) << "M/s) | Written: " << (writer.size() / 1000000) << "M / "
                              << (max_records / 1000000) << "M   " << std::flush;
                }
            }
        }
        writer.append_batch(local_batch);
    };

    std::vector<std::thread> threads;
    for (unsigned int t = 0; t < num_threads; ++t) threads.emplace_back(worker, t);
    for (auto& th : threads) th.join();

    writer.close();
    double dt = std::max(0.001, std::chrono::duration<double>(std::chrono::steady_clock::now() - start_time).count());
    std::cout << "\nDone! Wrote " << writer.size() << " clean V2 records to " << out_path
              << " in " << std::fixed << std::setprecision(1) << dt << "s ("
              << (double(writer.size()) / dt / 1e6) << "M written/s)\n";
    return 0;
}
