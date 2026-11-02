// Lưu từng câu trả lời: 200 học sinh làm bài cùng lúc, mỗi em lưu một câu khoảng 10 giây một lần, tổng khoảng 20 yêu cầu mỗi giây.
// Ngưỡng: lưu câu trả lời P95 <= 500 ms; không lỗi máy chủ; mọi lần lưu thành công.
//
// Bước vào bài (đăng nhập, bắt đầu lượt) chạy ở lượt lặp đầu của mỗi người dùng ảo và gắn nhãn chuan_bi, không tính vào số đo.
// Lượt chỉ được lưu, không nộp, nên không tiêu hao bài kiểm tra; nhưng lượt có hạn nộp theo thời gian làm của bài (README), chạy lại sau hạn thì tạo đợt bài mới.
import { sleep, check } from 'k6';
import exec from 'k6/execution';
import {
  layDuLieu, dangNhapHocSinh, batDauLuot, luuCau, hocSinhThu, kichBanNhe, luuThatBai,
} from './chung.js';

const THOI_GIAN = __ENV.THOI_GIAN || '3m';
const NHIP_GIAY = 10; // mỗi em lưu một câu mỗi 10 giây: 200 em / 10 giây = 20 yêu cầu mỗi giây

export const options = {
  scenarios: {
    luu_cau: __ENV.NHE
      ? Object.assign(kichBanNhe(), { exec: 'luuMotCau' })
      : { executor: 'constant-vus', vus: 200, duration: THOI_GIAN, exec: 'luuMotCau' },
  },
  thresholds: {
    'http_req_duration{name:luu_cau_tra_loi}': ['p(95)<500'],
    'http_reqs{name:luu_cau_tra_loi}': ['rate>=15'],
    loi_may_chu: ['count==0'],
    luu_that_bai: ['count==0'],
    bi_gioi_han_tan_suat: ['count==0'],
  },
};

export function setup() {
  layDuLieu();
}

// Trạng thái riêng của từng người dùng ảo, giữ giữa các lượt lặp.
let phien = null;

export function luuMotCau() {
  const d = layDuLieu();
  if (phien === null) {
    sleep(Math.random() * NHIP_GIAY); // rải đều giờ vào bài để các em không đăng nhập cùng một giây
    const hs = hocSinhThu(d, exec.vu.idInTest - 1);
    const token = dangNhapHocSinh(hs, { chuanBi: true });
    const luot = token ? batDauLuot(token, d.lop[hs.lop].baiKiemTra.luu, { chuanBi: true }) : null;
    phien = { token: token, luot: luot, i: 0 };
    return;
  }
  if (!phien.token || !phien.luot || !phien.luot.questions || phien.luot.questions.length === 0) {
    luuThatBai.add(1);
    sleep(NHIP_GIAY);
    return;
  }
  const cau = phien.luot.questions[phien.i % phien.luot.questions.length];
  phien.i += 1;
  const res = luuCau(phien.token, phien.luot.id, cau, 'luu_cau_tra_loi');
  if (!check(res, { 'luu cau tra loi 200': (r) => r.status === 200 })) luuThatBai.add(1);
  sleep(NHIP_GIAY);
}
