// Tìm, lọc và tải ghim bản đồ: 100 người dùng đồng thời.
// Ngưỡng: P95 <= 2 giây cho tìm, lọc và tải ghim; không lỗi máy chủ; mỗi lần tải ghim trả không quá 500 ghim.
//
// QUAN TRỌNG: giới hạn API công khai 300 yêu cầu mỗi phút mỗi IP (xem README); nới giới hạn trên máy thử hoặc chạy từ nhiều IP.
import { sleep, check } from 'k6';
import exec from 'k6/execution';
import { goi, duLieuJson, NGON_NGU, kichBanNhe } from './chung.js';

const THOI_GIAN = __ENV.THOI_GIAN || '2m';
const TU_KHOA = ['thang long', 'hue', 'chua', 'dinh', 'ha long', 'le hoi', 'thanh', 'lang'];
const VUNG = ['Bắc', 'Trung', 'Nam'];

export const options = {
  scenarios: {
    ban_do: __ENV.NHE
      ? Object.assign(kichBanNhe(), { exec: 'timLocBanDo' })
      : { executor: 'constant-vus', vus: 100, duration: THOI_GIAN, exec: 'timLocBanDo' },
  },
  thresholds: {
    'http_req_duration{name:tim_kiem}': ['p(95)<2000'],
    'http_req_duration{name:loc_theo_vung}': ['p(95)<2000'],
    'http_req_duration{name:tai_ghim_ban_do}': ['p(95)<2000'],
    'http_req_duration{name:tai_ghim_co_tu_khoa}': ['p(95)<2000'],
    loi_may_chu: ['count==0'],
    bi_gioi_han_tan_suat: ['count==0'],
    checks: ['rate==1'],
  },
};

function chon(mang) {
  return mang[Math.floor(Math.random() * mang.length)];
}

export function timLocBanDo() {
  const q = '&lang=' + encodeURIComponent(NGON_NGU[exec.vu.idInTest % NGON_NGU.length]);

  check(goi('GET', '/api/v1/public/heritages?keyword=' + encodeURIComponent(chon(TU_KHOA)) + '&page=1&pageSize=20' + q, 'tim_kiem'),
    { 'tim kiem 200': (r) => r.status === 200 });
  sleep(1 + Math.random() * 2);

  check(goi('GET', '/api/v1/public/heritages?region=' + encodeURIComponent(chon(VUNG)) + '&page=1&pageSize=20' + q, 'loc_theo_vung'),
    { 'loc 200': (r) => r.status === 200 });
  sleep(1 + Math.random() * 2);

  // Tải ghim cả nước (không phân trang, tối đa 500 ghim).
  const ghim = goi('GET', '/api/v1/public/heritages/map?x=1' + q, 'tai_ghim_ban_do');
  check(ghim, {
    'ghim 200': (r) => r.status === 200,
    'ghim khong qua 500': (r) => {
      const d = duLieuJson(r);
      return d !== null && d.pins.length <= 500;
    },
  });
  sleep(1 + Math.random() * 2);

  const ghimLoc = goi('GET', '/api/v1/public/heritages/map?region=' + encodeURIComponent(chon(VUNG)) + '&keyword=' + encodeURIComponent(chon(TU_KHOA)) + q,
    'tai_ghim_co_tu_khoa');
  check(ghimLoc, { 'ghim loc 200': (r) => r.status === 200 });
  sleep(1 + Math.random() * 2);
}
