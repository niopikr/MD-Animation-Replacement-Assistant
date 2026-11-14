import { convertSummon } from './summon-convert.js';

self.onmessage = ({ data }) => {
  try {
    const result = convertSummon(data.skeleton, data.atlas, progress => self.postMessage({ progress }));
    self.postMessage({ result });
  } catch (error) {
    self.postMessage({ error: error.message });
  }
};
