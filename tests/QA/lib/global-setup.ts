import { build } from './world';
// The product is built once from the same sources that are packaged.
export default async function globalSetup() { if (!process.env.QA_NO_BUILD) build(); }
