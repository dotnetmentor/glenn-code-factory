import { defineConfig } from 'orval'

const target = '../../swagger.json'

export default defineConfig({
  zod: {
    input: {
      target: target,
      validation: false,
    },
    output: {
      target: 'src/api/zod.ts',
      client: 'zod',
    },
  },
  api: {
    input: target,
    output: {
      target: 'src/api/queries-commands.ts',
      client: 'react-query',
      httpClient: 'axios',
      prettier: true,
      override: {
        mutator: {
          path: './src/api/mutator.ts',
          name: 'customClient',
        },
        query: {
          useQuery: true,
          useSuspenseQuery: false,
        },
      },
    },
  },
})
