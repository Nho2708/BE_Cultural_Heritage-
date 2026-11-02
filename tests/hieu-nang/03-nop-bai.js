// Nộp bài 20 câu: 200 học sinh nộp bài trong khoảng 30 giây.
// Ngưỡng: nộp bài P95 <= 2 giây; không lỗi máy chủ; 0 bài mất (nộp xong phải xem lại được kết quả).
//
// Mỗi người dùng ảo làm một lượt duy nhất: chuẩn bị (đăng nhập, bắt đầu lượt, lưu đủ 20 câu, nhãn chuan_bi, không tính vào số đo),
// chờ tới mốc MOC_NOP_GIAY (mặc định 150 giây kể từ lúc chạy), rồi nộp trong khung 30 giây.
// Mỗi lượt chỉ nộp được một lần: chạy lại thì phải tạo đợt bài mới (README, mục Chạy lại).
import { sleep, check } from 'k6';
import exec from 'k6/execution';
import {
  goi, duLieuJson, layDuLieu, dangNhapHocSinh, batDauLuot, luuCau, hocSinhThu, kichBanNhe, choDenMoc, baiMat,
} from './chung.js';

const MOC_NOP_MS = Number(__ENV.MOC_NOP_GIAY || 150) * 1000;
const KHUNG_NOP_GIAY = 30;

export const options = {
  scenarios: {
    nop_bai: __ENV.NHE
      ? Object.assign(kichBanNhe(), { exec: 'nopMotBai', iterations: 1 })
      : { executor: 'per-vu-iterations', vus: 200, iterations: 1, maxDuration: '10m', exec: 'nopMotBai' },
  },
  thresholds: {
    'http_req_duration{name:nop_bai}': ['p(95)<2000'],
    loi_may_chu: ['count==0'],
    bai_mat: ['count==0'],
    bi_gioi_han_tan_suat: ['count==0'],
  },
};

export function setup() {
  layDuLieu();
}

export function nopMotBai() {
  const d = layDuLieu();
  const hs = hocSinhThu(d, exec.vu.idInTest - 1);

  // Chuẩn bị, rải trong 60 giây đầu.
  sleep(Math.random() * Math.min(60, MOC_NOP_MS / 1000 / 2));
  const token = dangNhapHocSinh(hs, { chuanBi: true });
  const luot = token ? batDauLuot(token, d.lop[hs.lop].baiKiemTra.nop, { chuanBi: true }) : null;
  if (!luot || !luot.questions) {
    baiMat.add(1);
    return;
  }
  for (const cau of luot.questions) luuCau(token, luot.id, cau, 'luu_cau_chuan_bi', { chuanBi: true });

  // Chờ mốc rồi nộp, mỗi em một giờ ngẫu nhiên trong khung 30 giây.
  choDenMoc(MOC_NOP_MS);
  sleep(Math.random() * KHUNG_NOP_GIAY);
  const nop = goi('POST', '/api/v1/student/attempts/' + luot.id + '/submit', 'nop_bai', { token: token });
  let mat = nop.status !== 200;
  if (!mat) {
    const kq = goi('GET', '/api/v1/student/attempts/' + luot.id + '/result', 'xem_ket_qua', { token: token });
    mat = kq.status !== 200 || !duLieuJson(kq);
  }
  if (mat) baiMat.add(1);
  check(nop, { 'nop bai 200': (r) => r.status === 200 });
}
