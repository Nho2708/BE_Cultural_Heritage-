// Đăng nhập của học sinh, và kịch bản cả lớp vào làm bài.
//
//   KICH_BAN=dang_nhap (mặc định): 200 học sinh đăng nhập trong 60 giây, mỗi em đăng nhập một lần rồi mở trang học.
//   KICH_BAN=ca_lop: 5 lớp song song, mỗi lớp 40 em cùng lúc đăng nhập, mở trang học, mở bài bằng mã, xem trang giới thiệu bài kiểm tra,
//                    bắt đầu làm bài và lưu câu đầu. Cả 200 em dùng chung một địa chỉ IP (máy chạy k6), giống 40 em một lớp học.
//
// Ngưỡng: đăng nhập P95 <= 1 giây, trang học P95 <= 2 giây, 0 lỗi máy chủ, 0 em bị chặn nhầm.
import exec from 'k6/execution';
import { check } from 'k6';
import {
  goi, layDuLieu, dangNhapHocSinh, trangHoc, batDauLuot, luuCau, hocSinhThu, kichBanNhe,
} from './chung.js';

const kichBan = __ENV.KICH_BAN || 'dang_nhap';

function dangNhapScenarios() {
  if (__ENV.NHE) return { dang_nhap: Object.assign(kichBanNhe(), { exec: 'dangNhapMotEm' }) };
  return {
    dang_nhap: {
      executor: 'constant-arrival-rate',
      rate: 200, // 200 lượt đăng nhập
      timeUnit: '60s', // trong 60 giây
      duration: '60s',
      preAllocatedVUs: 50,
      maxVUs: 200,
      exec: 'dangNhapMotEm',
    },
  };
}

function caLopScenarios() {
  const s = {};
  for (let lop = 0; lop < 5; lop++) {
    s['lop_' + (lop + 1)] = {
      executor: 'shared-iterations',
      vus: 40,
      iterations: 40,
      maxDuration: '3m',
      exec: 'caLopMotEm',
      env: { LOP: String(lop) },
    };
  }
  return s;
}

export const options = {
  scenarios: kichBan === 'ca_lop' ? caLopScenarios() : dangNhapScenarios(),
  thresholds: {
    'http_req_duration{name:dang_nhap}': ['p(95)<1000'],
    'http_req_duration{name:trang_hoc}': ['p(95)<2000'],
    loi_may_chu: ['count==0'],
    hoc_sinh_bi_chan_nham: ['count==0'],
    bi_gioi_han_tan_suat: ['count==0'],
  },
};

export function setup() {
  layDuLieu();
}

export function dangNhapMotEm() {
  const d = layDuLieu();
  const hs = hocSinhThu(d, exec.scenario.iterationInTest);
  const token = dangNhapHocSinh(hs);
  if (!token) return;
  const res = trangHoc(token);
  check(res, { 'trang hoc mo duoc': (r) => r.status === 200 });
}

export function caLopMotEm() {
  const d = layDuLieu();
  const lop = Number(__ENV.LOP);
  const cua = d.hocSinh.filter((h) => h.lop === lop);
  if (cua.length === 0) exec.test.abort('Lớp ' + (lop + 1) + ' trong du-lieu.json không có học sinh');
  const hs = cua[exec.scenario.iterationInTest % cua.length];
  const info = d.lop[lop];

  const token = dangNhapHocSinh(hs);
  if (!token) return;
  check(trangHoc(token), { 'trang hoc mo duoc': (r) => r.status === 200 });
  check(goi('GET', '/api/v1/student/lessons/' + encodeURIComponent(info.maBaiHoc), 'mo_bai_bang_ma', { token: token }),
    { 'mo bai bang ma': (r) => r.status === 200 });
  goi('GET', '/api/v1/student/quizzes/' + info.baiKiemTra.vaoLop, 'gioi_thieu_bai', { token: token });
  const luot = batDauLuot(token, info.baiKiemTra.vaoLop);
  if (luot && luot.questions && luot.questions.length > 0) {
    check(luuCau(token, luot.id, luot.questions[0], 'luu_cau_tra_loi'), { 'luu cau dau': (r) => r.status === 200 });
  }
}
